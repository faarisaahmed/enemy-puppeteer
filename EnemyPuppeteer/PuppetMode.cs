using System;
using System.Linq;
using EnemyBehaviorApi.Runtime;
using EnemyBehaviorApi.Schema;
using UnityEngine;

namespace EnemyPuppeteer
{
    /// <summary>
    /// Turns the game into "you are this enemy": Hornet unhittable and unnoticed, camera on
    /// the puppet, and the puppet's body driven straight off the arrow keys.
    /// </summary>
    /// <remarks>
    /// This is the one part of the rig that reaches past the Enemy Behavior API and touches
    /// the game directly, and it is worth being clear why. The Override tier fires state
    /// transitions - it makes an enemy do a thing it already knows how to do. That is the
    /// right model for attacks and the wrong one for steering: walking left is not a state
    /// an enemy can be told to enter, it is a velocity held over time. So attacks still go
    /// through the API, while movement suppresses the FSM and writes the rigidbody.
    ///
    /// Everything it changes is recorded and restored in <see cref="Dispose"/>, because
    /// every one of these - invulnerability, hero layer, camera control - is state the
    /// player keeps living with if the rig forgets to hand it back.
    /// </remarks>
    public sealed class PuppetMode : IDisposable
    {
        /// <summary>Unity's built-in Ignore Raycast layer.</summary>
        private const int IgnoreRaycastLayer = 2;

        private const float DefaultSpeed = 8f;

        private readonly HeroController _hero;
        private readonly CameraController _cameraCtrl;
        private readonly Camera _camera;
        private readonly EnemyInstance _enemy;
        private readonly Rigidbody2D _body;

        private readonly int _heroLayer;
        private readonly bool _heroLayerChanged;
        private readonly bool _cameraWasEnabled;
        private readonly float _cameraZ;
        private readonly bool _bodyWasKinematic;
        private readonly float _bodyGravity;

        private bool _disposed;

        /// <summary>True when the puppet can move freely on both axes rather than only along the ground.</summary>
        public bool CanFly { get; }

        /// <summary>Speed used for manual steering, taken from the enemy's own movement data where possible.</summary>
        public float Speed { get; }

        /// <summary>Anything that could not be set up, for honest reporting in the UI.</summary>
        public string Limitations { get; private set; } = "";

        public PuppetMode(EnemyInstance enemy)
        {
            _enemy = enemy;
            _hero = UnityEngine.Object.FindObjectOfType<HeroController>();
            _cameraCtrl = UnityEngine.Object.FindObjectOfType<CameraController>();
            _camera = Camera.main;
            _body = enemy.GameObject.GetComponent<Rigidbody2D>();

            var profile = enemy.Profile;
            CanFly = profile != null && profile.Movements.Any(m =>
                m.Mode == MovementMode.Fly || m.IsAirborne);

            // Prefer a speed the enemy actually uses, so a fly moves like a fly.
            float discovered = profile?.Movements
                .Where(m => m.Speed > 0f)
                .Select(m => m.Speed)
                .DefaultIfEmpty(0f)
                .Max() ?? 0f;
            Speed = discovered > 0.5f ? Mathf.Min(discovered, 30f) : DefaultSpeed;

            // --- Hornet: unhittable ---
            if (_hero != null)
            {
                // Source-based, so this composes with anything else granting invulnerability
                // rather than stomping a shared flag.
                _hero.AddInvulnerabilitySource(this);

                // --- Hornet: unnoticed. Best-effort, see Limitations. ---
                _heroLayer = _hero.gameObject.layer;
                _hero.gameObject.layer = IgnoreRaycastLayer;
                _heroLayerChanged = true;
            }
            else
            {
                Limitations += "no HeroController found - Hornet is NOT protected; ";
            }

            // --- Camera: follow the puppet ---
            if (_cameraCtrl != null)
            {
                _cameraWasEnabled = _cameraCtrl.enabled;
                _cameraCtrl.enabled = false;
            }
            if (_camera != null) _cameraZ = _camera.transform.position.z;
            else Limitations += "no main camera - view will not follow; ";

            // --- Body: steerable ---
            if (_body != null)
            {
                _bodyWasKinematic = _body.isKinematic;
                _bodyGravity = _body.gravityScale;
                if (CanFly) _body.gravityScale = 0f;
            }
            else
            {
                Limitations += "enemy has no Rigidbody2D - arrow keys cannot move it; ";
            }
        }

        /// <summary>Writes the puppet's velocity from arrow-key input. Call from Update.</summary>
        /// <param name="input">-1..1 per axis. Vertical is ignored for non-flyers.</param>
        public void Drive(Vector2 input)
        {
            if (_disposed || _body == null || _enemy == null || !_enemy.IsAlive) return;

            float vx = input.x * Speed;
            // Grounded enemies keep their own vertical motion so gravity, jumps and knockback
            // still behave; only flyers get their height taken over.
            float vy = CanFly ? input.y * Speed : _body.linearVelocity.y;
            _body.linearVelocity = new Vector2(vx, vy);

            // Face the way we are steering. Enemies encode facing as the sign of local X
            // scale, which is also what their own FSMs drive, so this reads correctly to
            // their animations.
            if (Mathf.Abs(input.x) > 0.01f)
            {
                Transform t = _enemy.GameObject.transform;
                Vector3 s = t.localScale;
                float magnitude = Mathf.Abs(s.x);
                s.x = input.x < 0 ? -magnitude : magnitude;
                t.localScale = s;
            }
        }

        /// <summary>Keeps the camera centred on the puppet. Call from LateUpdate.</summary>
        public void FollowCamera()
        {
            if (_disposed || _camera == null || _enemy == null || !_enemy.IsAlive) return;
            Vector3 p = _enemy.GameObject.transform.position;
            _camera.transform.position = new Vector3(p.x, p.y, _cameraZ);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hero != null)
            {
                _hero.RemoveInvulnerabilitySource(this);
                if (_heroLayerChanged) _hero.gameObject.layer = _heroLayer;
            }

            if (_cameraCtrl != null) _cameraCtrl.enabled = _cameraWasEnabled;

            if (_body != null)
            {
                _body.gravityScale = _bodyGravity;
                _body.isKinematic = _bodyWasKinematic;
            }
        }
    }
}
