using System;
using System.Linq;
using EnemyBehaviorApi.Authority;
using EnemyBehaviorApi.Runtime;
using EnemyBehaviorApi.Schema;
using UnityEngine;

namespace EnemyPuppeteer
{
    /// <summary>
    /// Turns the game into "you are this enemy": Hornet parked and unhittable, camera on the
    /// puppet, and the puppet moving under its own walk cycle in whichever direction you point it.
    /// </summary>
    /// <remarks>
    /// Steering works by setting facing and letting the enemy's own movement state run,
    /// rather than by writing velocity. Writing velocity directly produces a creature
    /// sliding around under an idle animation - it moves, but nothing about it looks like
    /// walking, because the animation, the speed curve and the footfalls all live in the
    /// state that is no longer running. Team Cherry's movement actions are largely
    /// scale-relative (<c>SetVelocityByScale</c>, <c>DistanceWalk</c>), so flipping the
    /// enemy and letting its own walk state drive gives back the real gait for free.
    ///
    /// Everything changed here is recorded and restored in <see cref="Dispose"/>.
    /// </remarks>
    public sealed class PuppetMode : IDisposable
    {
        /// <summary>How long a fired movement state is left alone before being re-asserted.</summary>
        private const float RefireSeconds = 0.25f;

        private readonly HeroController _hero;
        private readonly CameraController _cameraCtrl;
        private readonly Camera _camera;
        private readonly EnemyInstance _enemy;
        private readonly IEnemyHandle _handle;
        private readonly Rigidbody2D _heroBody;
        private readonly Rigidbody2D _body;

        private readonly Vector3 _heroPosition;
        private readonly bool _heroWasKinematic;
        private readonly bool _cameraWasEnabled;
        private readonly float _cameraZ;
        private readonly float _bodyGravity;

        private readonly MovementDescriptor _walk;
        private readonly MovementDescriptor _idle;

        private float _nextFireAt;
        private string _lastFired;
        private bool _disposed;

        /// <summary>True when the puppet moves freely on both axes rather than only along the ground.</summary>
        public bool CanFly { get; }

        /// <summary>Flip the sign used for facing, for enemies whose art faces left at +X scale.</summary>
        public bool InvertFacing { get; set; }

        /// <summary>Anything that could not be set up, for honest reporting in the UI.</summary>
        public string Limitations { get; private set; } = "";

        /// <summary>What the puppet is doing right now, for the UI.</summary>
        public string Driving => _lastFired ?? "(nothing)";

        public PuppetMode(EnemyInstance enemy, IEnemyHandle handle)
        {
            _enemy = enemy;
            _handle = handle;
            _hero = UnityEngine.Object.FindObjectOfType<HeroController>();
            _cameraCtrl = UnityEngine.Object.FindObjectOfType<CameraController>();
            _camera = Camera.main;
            _body = enemy.GameObject.GetComponent<Rigidbody2D>();

            var profile = enemy.Profile;
            CanFly = profile != null && profile.Movements.Any(m => m.Mode == MovementMode.Fly || m.IsAirborne);

            // Prefer the enemy's own ordinary locomotion. Dash and Chase are last resorts:
            // they exist to close distance fast and read as lunging, not walking.
            _walk = Pick(profile, MovementMode.Walk)
                    ?? Pick(profile, MovementMode.Fly)
                    ?? Pick(profile, MovementMode.Chase)
                    ?? Pick(profile, MovementMode.Dash);
            _idle = Pick(profile, MovementMode.Idle);

            if (_walk == null) Limitations += "no walk state found - falling back to direct velocity; ";

            // --- Hornet: unhittable, and parked inside the puppet ---
            if (_hero != null)
            {
                _hero.AddInvulnerabilitySource(this);

                // Deliberately NOT a layer change. Moving her to Ignore Raycast stops enemies
                // seeing her but also stops terrain collision, so she falls out of the world.
                // Parking her on the puppet keeps her in bounds, keeps whatever lighting and
                // camera logic is anchored to the hero correct, and hides her inside the
                // enemy for free.
                _heroPosition = _hero.transform.position;
                _heroBody = _hero.GetComponent<Rigidbody2D>();
                if (_heroBody != null)
                {
                    _heroWasKinematic = _heroBody.isKinematic;
                    _heroBody.isKinematic = true;
                    _heroBody.linearVelocity = Vector2.zero;
                }
            }
            else
            {
                Limitations += "no HeroController found - Hornet is NOT protected; ";
            }

            if (_cameraCtrl != null)
            {
                _cameraWasEnabled = _cameraCtrl.enabled;
                _cameraCtrl.enabled = false;
            }
            if (_camera != null) _cameraZ = _camera.transform.position.z;
            else Limitations += "no main camera - view will not follow; ";

            if (_body != null) _bodyGravity = _body.gravityScale;
            else Limitations += "enemy has no Rigidbody2D; ";
        }

        private static MovementDescriptor Pick(EnemyProfile profile, MovementMode mode) =>
            profile?.Movements
                .Where(m => m.Mode == mode)
                .OrderByDescending(m => m.Confidence)
                .FirstOrDefault();

        /// <summary>Steers the puppet. Call from Update.</summary>
        /// <param name="input">-1..1 per axis. Vertical is ignored for non-flyers.</param>
        public void Drive(Vector2 input)
        {
            if (_disposed || _enemy == null || !_enemy.IsAlive) return;

            bool moving = Mathf.Abs(input.x) > 0.01f || (CanFly && Mathf.Abs(input.y) > 0.01f);

            if (Mathf.Abs(input.x) > 0.01f) Face(input.x);

            // Let the enemy walk itself. Its movement state carries the animation, the speed
            // and the feel; all we choose is which way it is pointed and when it goes.
            if (_walk != null)
            {
                var target = moving ? _walk : _idle;
                if (target != null && Time.time >= _nextFireAt && !IsActive(target))
                {
                    _nextFireAt = Time.time + RefireSeconds;
                    if (_handle.Fire(target.Id)) _lastFired = target.DisplayName;
                }

                // Flyers still need height steering - vertical is rarely a state of its own.
                if (CanFly && _body != null && Mathf.Abs(input.y) > 0.01f)
                    _body.linearVelocity = new Vector2(_body.linearVelocity.x, input.y * Mathf.Max(3f, _walk.Speed));

                return;
            }

            // No usable walk state: slide it, and say so in the UI rather than pretending.
            if (_body == null) return;
            float speed = Mathf.Max(4f, _idle?.Speed ?? 6f);
            _body.linearVelocity = new Vector2(
                input.x * speed,
                CanFly ? input.y * speed : _body.linearVelocity.y);
            _lastFired = "(direct velocity - no walk state)";
        }

        /// <summary>True when the FSM is already sitting in this behaviour's state.</summary>
        private bool IsActive(BehaviorDescriptor behavior)
        {
            var fsm = _enemy.ResolveFsm(behavior.State);
            return fsm != null && fsm.ActiveStateName == behavior.State.StateName;
        }

        /// <summary>
        /// Points the enemy along <paramref name="direction"/> by the sign of its local X scale.
        /// </summary>
        /// <remarks>
        /// That is the same channel the enemy's own FSM uses to turn, which is why its
        /// scale-relative movement actions then carry it the right way. Which sign means
        /// "right" is per-enemy art, hence <see cref="InvertFacing"/>.
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

        /// <summary>Keeps the camera on the puppet and Hornet parked inside it. Call from LateUpdate.</summary>
        public void FollowCamera()
        {
            if (_disposed || _enemy == null || !_enemy.IsAlive) return;
            Vector3 p = _enemy.GameObject.transform.position;

            if (_camera != null) _camera.transform.position = new Vector3(p.x, p.y, _cameraZ);

            // Hornet rides along, so anything anchored to the hero - lighting, audio
            // listeners, camera bounds - stays where the action is.
            if (_hero != null) _hero.transform.position = new Vector3(p.x, p.y, _hero.transform.position.z);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hero != null)
            {
                _hero.RemoveInvulnerabilitySource(this);
                _hero.transform.position = _heroPosition;
                if (_heroBody != null) _heroBody.isKinematic = _heroWasKinematic;
            }

            if (_cameraCtrl != null) _cameraCtrl.enabled = _cameraWasEnabled;
            if (_body != null) _body.gravityScale = _bodyGravity;
        }
    }
}
