using System;
using System.Collections.Generic;
using System.Linq;
using EnemyBehaviorApi.Authority;
using EnemyBehaviorApi.Runtime;
using EnemyBehaviorApi.Schema;
using UnityEngine;

namespace EnemyPuppeteer
{
    /// <summary>
    /// Turns Hornet into an invisible marker and walks the enemy to it, leaving every
    /// attack to the player.
    /// </summary>
    /// <remarks>
    /// The enemy's own AI is deliberately not in charge. Letting it hunt Hornet by itself
    /// looks convincing but means the *AI* is playing - it picks the attack, the timing and
    /// the spacing, and a player driving it is reduced to placing bait. For a fight between
    /// two people that is not enough: whoever is driving needs to choose when to commit.
    ///
    /// So decisions are suppressed and the enemy is walked manually. Locomotion reuses the
    /// patrol walk - the unhurried back-and-forth an enemy does before it has noticed
    /// anything - because it is the one movement nearly every enemy has, it is not tied to
    /// a target, and it carries the creature's normal gait and speed. Pointing the enemy and
    /// running that state gets honest movement for free; Team Cherry's movement actions are
    /// largely scale-relative, so facing is the steering wheel.
    ///
    /// Awareness follows from the same mechanism rather than being faked: with its decision
    /// states suppressed, the enemy cannot act on noticing Hornet. It only reaches her when
    /// the player fires an attack, which aims at wherever the marker is standing.
    ///
    /// Everything changed here is recorded and restored in <see cref="Dispose"/>.
    /// </remarks>
    public sealed class PuppetMode : IDisposable
    {
        private readonly HeroController _hero;
        private readonly CameraController _cameraCtrl;
        private readonly Camera _camera;
        private readonly EnemyInstance _enemy;
        private readonly IEnemyHandle _handle;
        private readonly Rigidbody2D _heroBody;

        private readonly Vector3 _heroStart;
        private readonly bool _heroWasKinematic;
        private readonly bool _cameraWasEnabled;
        private readonly float _cameraZ;
        private readonly List<KeyValuePair<Renderer, bool>> _hiddenRenderers = new List<KeyValuePair<Renderer, bool>>();

        private readonly MovementDescriptor _patrolWalk;
        private readonly MovementDescriptor _idle;

        private Vector3 _lure;
        private float _nextFireAt;
        private bool _disposed;

        /// <summary>How close counts as arrived, so the enemy stops rather than jittering on the spot.</summary>
        private const float ArriveDistance = 1.2f;

        private const float RefireSeconds = 0.2f;

        /// <summary>Where Hornet is standing, which is what the enemy is hunting.</summary>
        public Vector3 Lure => _lure;

        /// <summary>Anything that could not be set up, for honest reporting in the UI.</summary>
        public string Limitations { get; private set; } = "";

        /// <summary>The walk being used to move, for the UI.</summary>
        public string WalkName => _patrolWalk?.DisplayName ?? "(none found)";

        /// <summary>Flip the sign used for facing, for enemies whose art faces left at +X scale.</summary>
        public bool InvertFacing { get; set; }

        /// <summary>True while the enemy is being walked toward the marker.</summary>
        public bool IsWalking { get; private set; }

        public PuppetMode(EnemyInstance enemy, IEnemyHandle handle)
        {
            _enemy = enemy;
            _handle = handle;
            _hero = UnityEngine.Object.FindObjectOfType<HeroController>();
            _cameraCtrl = UnityEngine.Object.FindObjectOfType<CameraController>();
            _camera = Camera.main;

            if (_hero != null)
            {
                _heroStart = _hero.transform.position;
                _lure = _heroStart;

                // Source-based, so both of these compose with anything else that grants
                // invulnerability or blocks input rather than stomping a shared flag.
                _hero.AddInvulnerabilitySource(this);
                _hero.AddInputBlocker(this);

                // Invisible rather than layer-swapped. Changing her layer stops enemies
                // seeing her but also removes terrain collision, which drops her out of the
                // world; hiding the renderers leaves all her physics and targeting intact,
                // which is exactly what we still need her for.
                foreach (var r in _hero.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    _hiddenRenderers.Add(new KeyValuePair<Renderer, bool>(r, r.enabled));
                    r.enabled = false;
                }

                _heroBody = _hero.GetComponent<Rigidbody2D>();
                if (_heroBody != null)
                {
                    _heroWasKinematic = _heroBody.isKinematic;
                    // Kinematic so she stays exactly where placed - no falling, no being
                    // shoved around by the enemy that is attacking her.
                    _heroBody.isKinematic = true;
                    _heroBody.linearVelocity = Vector2.zero;
                }
            }
            else
            {
                Limitations += "no HeroController - cannot hide or protect Hornet; ";
            }

            if (_cameraCtrl != null)
            {
                _cameraWasEnabled = _cameraCtrl.enabled;
                _cameraCtrl.enabled = false;
            }
            if (_camera != null) _cameraZ = _camera.transform.position.z;
            else Limitations += "no main camera - view will not follow; ";

            // The patrol walk: ordinary locomotion that does not chase. Untargeted states
            // are preferred precisely because a chase state would drag the enemy at the
            // marker on its own terms, which is what we are taking away from it.
            var profile = enemy.Profile;
            _patrolWalk = PickWalk(profile, targeted: false) ?? PickWalk(profile, targeted: true);
            _idle = profile?.Movements
                .Where(m => m.Mode == MovementMode.Idle)
                .OrderByDescending(m => m.Confidence)
                .FirstOrDefault();

            if (_patrolWalk == null) Limitations += "no walk state found - this enemy cannot be moved; ";

            // Decisions suppressed: the enemy keeps running states, but stops choosing them.
            // This is what makes it unaware in practice - it can notice Hornet all it likes,
            // it just cannot act on it until the player fires something.
            handle.Policy = OverridePolicy.SuppressDecisions;
            handle.SetSuppressionActive(true);
        }

        /// <summary>
        /// The enemy's ordinary walk: ground movement first, then flight, ignoring dashes
        /// and jumps which read as committed lunges rather than travel.
        /// </summary>
        private static MovementDescriptor PickWalk(EnemyProfile profile, bool targeted)
        {
            if (profile == null) return null;
            foreach (var mode in new[] { MovementMode.Walk, MovementMode.Fly, MovementMode.Chase })
            {
                var found = profile.Movements
                    .Where(m => m.Mode == mode && m.IsTargeted == targeted)
                    .OrderByDescending(m => m.Confidence)
                    .FirstOrDefault();
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Walks the enemy one step toward the marker. Call from Update.
        /// </summary>
        /// <remarks>
        /// Only facing and "is the walk running" are driven. How fast it goes, how it
        /// animates and how it handles ground are all left to the enemy's own state, which
        /// is the only way the movement reads as that creature walking rather than an object
        /// being dragged.
        /// </remarks>
        public void StepTowardMarker()
        {
            IsWalking = false;
            if (_disposed || _patrolWalk == null || _enemy == null || !_enemy.IsAlive) return;

            Vector3 here = _enemy.GameObject.transform.position;
            float dx = _lure.x - here.x;

            if (Mathf.Abs(dx) <= ArriveDistance)
            {
                if (_idle != null && !IsActive(_idle) && Time.time >= _nextFireAt)
                {
                    _nextFireAt = Time.time + RefireSeconds;
                    _handle.Fire(_idle.Id);
                }
                return;
            }

            Face(dx);
            IsWalking = true;

            if (!IsActive(_patrolWalk) && Time.time >= _nextFireAt)
            {
                _nextFireAt = Time.time + RefireSeconds;
                _handle.Fire(_patrolWalk.Id);
            }
        }

        private bool IsActive(BehaviorDescriptor behavior)
        {
            var fsm = _enemy.ResolveFsm(behavior.State);
            return fsm != null && fsm.ActiveStateName == behavior.State.StateName;
        }

        /// <summary>
        /// Points the enemy along <paramref name="direction"/> via the sign of local X scale.
        /// </summary>
        /// <remarks>
        /// The same channel the enemy's own FSM turns with, which is why its scale-relative
        /// movement actions then carry it the right way. Which sign means "right" is
        /// per-enemy art, hence <see cref="InvertFacing"/>.
        /// </remarks>
        private void Face(float direction)
        {
            Transform t = _enemy.GameObject.transform;
            Vector3 s = t.localScale;
            float magnitude = Mathf.Abs(s.x);
            bool wantPositive = InvertFacing ? direction < 0f : direction > 0f;
            s.x = wantPositive ? magnitude : -magnitude;
            t.localScale = s;
        }

        /// <summary>The marker's position on screen, for drawing it. Null if off-camera.</summary>
        public Vector2? MarkerOnScreen()
        {
            if (_disposed || _camera == null) return null;
            Vector3 p = _camera.WorldToScreenPoint(_lure);
            if (p.z < 0f) return null;
            return new Vector2(p.x, Screen.height - p.y);
        }

        /// <summary>Moves the marker to a world position.</summary>
        public void PlaceMarker(Vector3 worldPosition)
        {
            if (_disposed || _hero == null) return;
            _lure = new Vector3(worldPosition.x, worldPosition.y, _hero.transform.position.z);
            _hero.transform.position = _lure;
            if (_heroBody != null) _heroBody.linearVelocity = Vector2.zero;
        }

        /// <summary>Holds Hornet on the lure and keeps the camera on the enemy. Call from LateUpdate.</summary>
        /// <remarks>
        /// Re-asserted every frame because plenty of things in the game move the hero -
        /// knockback, conveyors, an enemy walking into her - and any of them would otherwise
        /// drag the lure around mid-fight.
        /// </remarks>
        public void HoldPosition()
        {
            if (_disposed) return;

            if (_hero != null) _hero.transform.position = _lure;

            if (_camera != null && _enemy != null && _enemy.IsAlive)
            {
                Vector3 p = _enemy.GameObject.transform.position;
                _camera.transform.position = new Vector3(p.x, p.y, _cameraZ);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hero != null)
            {
                _hero.RemoveInvulnerabilitySource(this);
                _hero.RemoveInputBlocker(this);
                _hero.transform.position = _heroStart;
                if (_heroBody != null) _heroBody.isKinematic = _heroWasKinematic;
            }

            foreach (var entry in _hiddenRenderers)
            {
                if (entry.Key != null) entry.Key.enabled = entry.Value;
            }
            _hiddenRenderers.Clear();

            if (_cameraCtrl != null) _cameraCtrl.enabled = _cameraWasEnabled;
        }
    }
}
