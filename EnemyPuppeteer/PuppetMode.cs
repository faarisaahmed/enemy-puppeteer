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
    /// Turns Hornet into an invisible lure and lets the enemy hunt her under its own AI.
    /// </summary>
    /// <remarks>
    /// Steering an enemy by writing velocity, or by firing movement states at it, both fight
    /// the creature instead of using it. Enemies already know how to approach Hornet - that
    /// is most of what their FSMs do - and doing it themselves gets the right gait, the
    /// right approach distance, the right attack spacing and the right animations, none of
    /// which a puppeteer can fake convincingly.
    ///
    /// So the enemy is left running its own AI, and what gets moved is the target. Hornet
    /// is made invulnerable, invisible, and unable to act, then parked wherever you click;
    /// the enemy walks over and attacks because that is what it would do anyway.
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

        private Vector3 _lure;
        private bool _disposed;

        /// <summary>Where Hornet is standing, which is what the enemy is hunting.</summary>
        public Vector3 Lure => _lure;

        /// <summary>Anything that could not be set up, for honest reporting in the UI.</summary>
        public string Limitations { get; private set; } = "";

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

            // The enemy keeps making its own decisions. Override is still claimed so attacks
            // can be fired by hand, but suppressing its decisions would stop it noticing or
            // approaching the lure, which is the whole mechanism.
            handle.SetSuppressionActive(false);
        }

        /// <summary>Moves the lure to a world position. The enemy takes it from there.</summary>
        public void PlaceLure(Vector3 worldPosition)
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
