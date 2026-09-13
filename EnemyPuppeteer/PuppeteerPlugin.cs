using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using EnemyBehaviorApi;
using EnemyBehaviorApi.Authority;
using EnemyBehaviorApi.Runtime;
using EnemyBehaviorApi.Schema;
using UnityEngine;

namespace EnemyPuppeteer
{
    /// <summary>
    /// An on-screen rig for driving one enemy by hand: pick it, fire its attacks, steer it
    /// with the arrow keys.
    /// </summary>
    /// <remarks>
    /// Built to exercise the two Enemy Behavior API tiers that no automated run has ever
    /// touched. Influence and Override are the parts that can visibly break a fight - an
    /// Override holder that swallows a death transition leaves an enemy standing in an
    /// unkillable loop - and that class of bug is far easier to *feel* than to assert in a
    /// test. Driving an enemy by hand for a minute says more than a pass/fail line.
    ///
    /// Everything here goes through the public API. Nothing touches PlayMaker directly,
    /// which is the point: if this mod can do it, so can anyone else's.
    /// </remarks>
    [BepInPlugin(Guid, "Enemy Puppeteer", "0.1.0")]
    [BepInDependency("com.faaris.enemybehaviorapi")]
    public sealed class PuppeteerPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.faaris.enemypuppeteer";

        private const int MaxLogLines = 8;

        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<float> _pickRadius;
        private ConfigEntry<bool> _soloMode;

        private readonly List<string> _log = new List<string>();
        private IEnemyHandle _handle;
        private PuppetController _puppet;
        private PuppetMode _mode;
        private BystanderSuppressor _bystanders;
        private EnemyInstance _selected;
        private Vector2 _scroll;
        private bool _open;
        private Rect _window = new Rect(20, 20, 460, 560);

        private void Awake()
        {
            _toggleKey = Config.Bind("Puppeteer", "ToggleKey", KeyCode.F8,
                "Opens and closes the puppeteer window. Control is released when it closes.");
            _pickRadius = Config.Bind("Puppeteer", "ClickRadius", 2.5f,
                "How close to an enemy, in world units, a click has to land to select it.");

            _soloMode = Config.Bind("Puppeteer", "OnlyControlledEnemyReacts", true,
                "While puppeteering, stop every other enemy reacting to Hornet. The marker is a real " +
                "targetable Hornet, so without this the whole room converges on it and the duel becomes " +
                "whatever the scene happened to contain.");

            Logger.LogInfo($"Enemy Puppeteer ready - press {_toggleKey.Value}");
        }

        private void OnDestroy() => ReleaseControl();

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey.Value))
            {
                _open = !_open;
                // Never leave an enemy under suppression because a window got closed.
                if (!_open) ReleaseControl();
            }

            if (!_open) return;

            bool overWindow = _window.Contains(GuiMousePosition());

            // While puppeteering, left-click moves the lure - that is the main verb, so it
            // gets the main button. Selection moves to right-click for the duration.
            if (Input.GetMouseButtonDown(0) && !overWindow)
            {
                if (_mode != null) PlaceMarkerUnderMouse();
                else PickUnderMouse();
            }
            else if (Input.GetMouseButtonDown(1) && !overWindow)
            {
                PickUnderMouse();
            }

            if (_handle == null || !_handle.IsValid || _handle.Tier != AuthorityTier.Override) return;

            // Walk toward the marker every frame. Attacks stay entirely manual - the point
            // of driving an enemy is choosing when it commits.
            _mode?.StepTowardMarker();
        }

        private void LateUpdate() => _mode?.HoldPosition();

        /// <summary>Puts the marker where the cursor is, so the enemy walks there.</summary>
        /// <remarks>
        /// The depth has to be filled in before converting. <c>Input.mousePosition</c> has
        /// z = 0, and on a perspective camera that resolves to a point essentially at the
        /// camera itself - which maps back to the centre of the screen no matter where you
        /// click. Supplying the distance to the plane the enemy is standing on fixes it,
        /// and is harmless on an orthographic camera.
        /// </remarks>
        private void PlaceMarkerUnderMouse()
        {
            Camera cam = Camera.main;
            if (cam == null || _mode == null || _selected == null || !_selected.IsAlive) return;

            Vector3 world = CursorWorld(cam, _selected.GameObject.transform.position.z);
            _mode.PlaceMarker(world);
            Note($"marker -> ({world.x:0.0}, {world.y:0.0})");
        }

        // ---- Selection ------------------------------------------------------------------

        private static Vector2 GuiMousePosition() =>
            new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);

        /// <summary>The world point under the cursor, on the plane at <paramref name="planeZ"/>.</summary>
        /// <remarks>
        /// The depth must be supplied before converting. <c>Input.mousePosition</c> carries
        /// z = 0, and on a perspective camera that resolves to a point essentially at the
        /// camera - which maps back to the centre of the screen wherever you click. Both
        /// clicking an enemy and placing the marker were wrong for this reason, the first
        /// one subtly enough to look like a picking-radius problem.
        /// </remarks>
        private static Vector3 CursorWorld(Camera cam, float planeZ)
        {
            Vector3 screen = Input.mousePosition;
            screen.z = Mathf.Abs(cam.transform.position.z - planeZ);
            Vector3 world = cam.ScreenToWorldPoint(screen);
            world.z = planeZ;
            return world;
        }

        /// <summary>
        /// Selects whatever enemy the cursor is physically over.
        /// </summary>
        /// <remarks>
        /// Three passes, most precise first. Picking by distance to
        /// <c>transform.position</c> alone - which is all this used to do - goes wrong in
        /// two ways that matter: an enemy whose pivot sits at its feet or off inside a wall
        /// reads as further away than it looks, and in a crowd the nearest pivot is often
        /// not the body under the cursor.
        /// </remarks>
        private void PickUnderMouse()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            // Resolve on the plane the enemies are actually standing on, not z = 0.
            float planeZ = EnemyBehavior.ActiveEnemies
                .Where(e => e.IsAlive)
                .Select(e => e.GameObject.transform.position.z)
                .DefaultIfEmpty(0f)
                .First();

            Vector3 world = CursorWorld(cam, planeZ);
            var point = new Vector2(world.x, world.y);

            // 1. A real hit on a collider. Enemy hurtboxes are frequently triggers, and
            // the project setting for whether queries see triggers is not ours to assume.
            bool previous = Physics2D.queriesHitTriggers;
            Physics2D.queriesHitTriggers = true;
            try
            {
                foreach (var hit in Physics2D.OverlapPointAll(point, ~0))
                {
                    var owner = OwningEnemy(hit != null ? hit.transform : null);
                    if (owner != null) { Select(owner); return; }
                }
            }
            finally { Physics2D.queriesHitTriggers = previous; }

            // 2. Inside an enemy's collider bounds, for colliders a point query skipped
            // (disabled during part of an attack, on an ignored layer, and so on).
            foreach (var enemy in EnemyBehavior.ActiveEnemies)
            {
                if (!enemy.IsAlive) continue;
                foreach (var col in enemy.GameObject.GetComponentsInChildren<Collider2D>(true))
                {
                    if (col == null || !col.bounds.Contains(new Vector3(point.x, point.y, col.bounds.center.z))) continue;
                    Select(enemy);
                    return;
                }
            }

            // 3. Nearest pivot, as a last resort.
            EnemyInstance best = null;
            float bestDistance = _pickRadius.Value;
            foreach (var enemy in EnemyBehavior.ActiveEnemies)
            {
                if (!enemy.IsAlive) continue;
                float d = Vector2.Distance(point, enemy.GameObject.transform.position);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = enemy;
            }

            if (best != null) Select(best);
            else Note("nothing under the cursor");
        }

        /// <summary>Walks up from a collider to the registered enemy that owns it, if any.</summary>
        /// <remarks>
        /// Hitboxes hang off children - often several levels down - so the object a point
        /// query returns is almost never the one the API registered.
        /// </remarks>
        private static EnemyInstance OwningEnemy(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                var found = EnemyBehavior.GetInstance(t.gameObject);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Registers anything damageable in the scene that the API has not already picked up.
        /// </summary>
        /// <remarks>
        /// Enemies register when their HealthManager starts. Anything already awake before
        /// the API loaded, or spawned by a path that does not run that hook, is simply
        /// absent from the list - which looks like the mod missing enemies that are plainly
        /// on screen.
        /// </remarks>
        private void RescanScene()
        {
            int before = EnemyBehavior.ActiveEnemies.Count();
            int added = 0;

            foreach (var health in FindObjectsOfType<HealthManager>())
            {
                if (health == null) continue;
                if (EnemyBehavior.GetInstance(health.gameObject) != null) continue;
                if (EnemyBehavior.Track(health.gameObject) != null) added++;
            }

            Note($"rescan: {before} known, {added} added");
        }

        private void Select(EnemyInstance enemy)
        {
            if (_selected != null && _selected.InstanceId == enemy.InstanceId) return;

            ReleaseControl();
            _selected = enemy;
            Note($"selected {enemy.Profile?.EnemyId ?? "?"}");
        }

        // ---- Authority ------------------------------------------------------------------

        private void TakeControl()
        {
            if (_selected == null || !_selected.IsAlive) return;
            ReleaseControl();

            string incumbent = EnemyBehavior.OverrideHolder(_selected);
            if (incumbent != null && incumbent != Guid) Note($"'{incumbent}' already drives this one");

            _handle = EnemyBehavior.Claim(_selected, Guid, AuthorityTier.Override);
            if (_handle == null)
            {
                Note("claim failed - the API returned no handle");
                return;
            }

            _puppet = new PuppetController(_handle.Profile);
            _handle.StateChanged += OnStateChanged;

            if (_handle.Tier == AuthorityTier.Override)
            {
                _mode = new PuppetMode(_selected, _handle);
                if (!string.IsNullOrEmpty(_mode.Limitations)) Note("limits: " + _mode.Limitations);

                if (_soloMode.Value)
                    _bystanders = new BystanderSuppressor(Guid, _selected, Note);
            }

            Note(_handle.Tier == AuthorityTier.Override
                ? $"took control ({_handle.Policy})"
                : $"only got {_handle.Tier} - someone else holds Override");
        }

        private void ReleaseControl()
        {
            _mode?.Dispose();
            _mode = null;

            _bystanders?.Dispose();
            _bystanders = null;

            if (_handle == null) return;
            _handle.StateChanged -= OnStateChanged;
            _handle.Dispose();
            _handle = null;
            _puppet = null;
            Note("released");
        }

        private void OnStateChanged(object sender, StateChangedEventArgs e)
        {
            if (e.Behavior == null) return;
            Note($"  -> {e.State.StateName}{(e.WasDriven ? " (ours)" : " (its own)")}");
        }

        private void Note(string message)
        {
            _log.Add($"[{Time.time:0.0}] {message}");
            if (_log.Count > MaxLogLines) _log.RemoveAt(0);
            Logger.LogInfo(message);
        }

        // ---- UI -------------------------------------------------------------------------

        private void OnGUI()
        {
            if (!_open) return;
            DrawMarker();
            _window = GUI.Window(GetInstanceID(), _window, DrawWindow, "Enemy Puppeteer");
        }

        /// <summary>
        /// Draws a crosshair where the marker is standing.
        /// </summary>
        /// <remarks>
        /// Hornet is invisible there, so without this the enemy walks toward a spot with
        /// nothing visible in it and there is no way to tell a missed click from an enemy
        /// that cannot move.
        /// </remarks>
        private void DrawMarker()
        {
            Vector2? at = _mode?.MarkerOnScreen();
            if (at == null) return;

            Vector2 p = at.Value;
            Color previous = GUI.color;
            GUI.color = _mode.IsWalking ? new Color(1f, 0.85f, 0.2f, 0.95f) : new Color(1f, 1f, 1f, 0.6f);

            const float arm = 11f, thick = 2f;
            GUI.DrawTexture(new Rect(p.x - arm, p.y - thick * 0.5f, arm * 2f, thick), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(p.x - thick * 0.5f, p.y - arm, thick, arm * 2f), Texture2D.whiteTexture);

            GUI.color = previous;
        }

        private void DrawWindow(int id)
        {
            if (!EnemyBehavior.IsReady)
            {
                GUILayout.Label("Enemy Behavior API is not ready.");
                GUI.DragWindow();
                return;
            }

            var enemies = EnemyBehavior.ActiveEnemies.Where(e => e.IsAlive).ToList();
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{enemies.Count} enemy instance(s) - click one in the world, or pick below");
            if (GUILayout.Button("Rescan", GUILayout.Width(70))) RescanScene();
            GUILayout.EndHorizontal();

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(130));
            foreach (var enemy in enemies)
            {
                bool isSelected = _selected != null && _selected.InstanceId == enemy.InstanceId;
                string holder = EnemyBehavior.OverrideHolder(enemy);
                string label = $"{(isSelected ? "> " : "  ")}{enemy.Profile?.EnemyId} " +
                               $"[{enemy.Profile?.Actions.Count ?? 0} atk / {enemy.Profile?.Movements.Count ?? 0} mov]" +
                               (holder != null ? "  (driven)" : "");
                if (GUILayout.Button(label, GUILayout.Height(20))) Select(enemy);
            }
            GUILayout.EndScrollView();

            GUILayout.Space(6);
            DrawSelected();

            GUILayout.Space(6);
            GUILayout.Label("--- log ---");
            foreach (string line in _log) GUILayout.Label(line);

            GUI.DragWindow();
        }

        private void DrawSelected()
        {
            if (_selected == null || !_selected.IsAlive)
            {
                GUILayout.Label("Nothing selected.");
                return;
            }

            var profile = _selected.Profile;
            bool driving = _handle != null && _handle.IsValid && _handle.Tier == AuthorityTier.Override;

            GUILayout.Label($"{profile?.EnemyId}   {profile?.MaxHealth} hp   fsm: {profile?.PrimaryFsm}");

            // Live state is the honest readout of whether anything we fire actually lands.
            var live = _selected.PrimaryFsm;
            GUILayout.Label($"state: {live?.ActiveStateName ?? "?"}");

            GUILayout.BeginHorizontal();
            if (!driving && GUILayout.Button("Take control")) TakeControl();
            if (driving && GUILayout.Button("Release")) ReleaseControl();
            if (driving && GUILayout.Button($"Policy: {_handle.Policy}"))
                _handle.Policy = _handle.Policy == OverridePolicy.SuppressDecisions
                    ? OverridePolicy.SuppressAll
                    : OverridePolicy.SuppressDecisions;
            GUILayout.EndHorizontal();

            if (!driving)
            {
                GUILayout.Label("Take control to fire attacks and steer.");
                return;
            }

            GUILayout.Space(4);
            GUILayout.Label("Attacks - click to fire:");
            DrawAttackButtons(profile);

            GUILayout.Space(4);
            if (_mode != null)
            {
                GUILayout.Label("PUPPET MODE - Hornet is an invisible marker; you drive everything.");
                GUILayout.Label("   LEFT-CLICK to move the marker - the enemy walks to it");
                GUILayout.Label("   RIGHT-CLICK to select a different enemy");
                GUILayout.Label($"   walking with: {_mode.WalkName}{(_mode.IsWalking ? "  [moving]" : "")}");
                if (GUILayout.Button(_mode.InvertFacing ? "Facing: inverted" : "Facing: normal"))
                    _mode.InvertFacing = !_mode.InvertFacing;
                if (!string.IsNullOrEmpty(_mode.Limitations))
                    GUILayout.Label("   limits: " + _mode.Limitations);

                GUILayout.BeginHorizontal();
                if (GUILayout.Button(_soloMode.Value
                        ? $"Bystanders: held ({_bystanders?.Count ?? 0})"
                        : "Bystanders: free"))
                {
                    _soloMode.Value = !_soloMode.Value;
                    if (_soloMode.Value)
                    {
                        _bystanders = new BystanderSuppressor(Guid, _selected, Note);
                    }
                    else
                    {
                        _bystanders?.Dispose();
                        _bystanders = null;
                        Note("bystanders released - the whole room can see Hornet again");
                    }
                }
                GUILayout.EndHorizontal();
                if (_bystanders != null && _bystanders.Contested > 0)
                    GUILayout.Label($"   {_bystanders.Contested} controlled by another mod - not held");

                GUILayout.Space(4);
                GUILayout.Label("Movement states - for ledges, gaps and repositioning:");
                DrawMovementButtons(profile);
            }
            else
            {
                GUILayout.Label("Arrow keys fire this enemy's own movement states:");
                foreach (Intent intent in new[] { Intent.Left, Intent.Right, Intent.Up, Intent.Down })
                {
                    var bound = _puppet?.Preview(intent);
                    GUILayout.Label($"   {intent,-6} {(bound != null ? $"{bound.DisplayName} ({bound.Mode})" : "- nothing bound -")}");
                }
            }
        }

        /// <summary>
        /// Jumps, dashes and the like, as buttons.
        /// </summary>
        /// <remarks>
        /// The walk handles flat ground; anything else - a ledge, a gap, closing distance
        /// fast - is the player's call, same as an attack. Idle and plain walks are left out
        /// since the marker already drives those.
        /// </remarks>
        private void DrawMovementButtons(EnemyProfile profile)
        {
            var interesting = profile?.Movements
                .Where(m => m.Mode == MovementMode.Jump || m.Mode == MovementMode.Dash ||
                            m.Mode == MovementMode.Teleport)
                .OrderByDescending(m => m.Confidence)
                .ToList();

            if (interesting == null || interesting.Count == 0)
            {
                GUILayout.Label("   (none - this enemy only walks)");
                return;
            }

            int column = 0;
            GUILayout.BeginHorizontal();
            foreach (var move in interesting)
            {
                if (GUILayout.Button($"{move.DisplayName}\n{move.Mode}", GUILayout.Height(30)))
                {
                    bool ok = _handle.Fire(move.Id);
                    Note($"{(ok ? "move" : "MOVE FAILED")}: {move.DisplayName}");
                }
                if (++column % 3 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            }
            GUILayout.EndHorizontal();
        }

        private void DrawAttackButtons(EnemyProfile profile)
        {
            if (profile == null || profile.Actions.Count == 0)
            {
                GUILayout.Label("   (discovery found no attacks on this enemy)");
                return;
            }

            int column = 0;
            GUILayout.BeginHorizontal();
            foreach (var attack in profile.Actions.OrderByDescending(a => a.Confidence))
            {
                // Wind-ups are listed but marked: firing one skips straight to a tell with
                // no strike, which is a legitimate thing to try and a confusing thing to
                // hit by accident.
                string label = attack.TelegraphFor != null
                    ? $"{attack.DisplayName} (antic)"
                    : attack.DisplayName;

                if (GUILayout.Button($"{label}\n{attack.Shape} - {attack.Tier}", GUILayout.Height(34)))
                {
                    bool ok = _handle.Fire(attack.Id);
                    Note($"{(ok ? "fire" : "FIRE FAILED")}: {attack.DisplayName}" +
                         (attack.DamageAmount > 0 ? $" ({attack.DamageAmount} dmg)" : ""));
                }

                if (++column % 2 == 0)
                {
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                }
            }
            GUILayout.EndHorizontal();
        }
    }
}
