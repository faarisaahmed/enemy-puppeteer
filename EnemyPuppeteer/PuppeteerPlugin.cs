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

        private readonly List<string> _log = new List<string>();
        private IEnemyHandle _handle;
        private PuppetController _puppet;
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

            if (Input.GetMouseButtonDown(0) && !_window.Contains(GuiMousePosition())) PickUnderMouse();

            if (_handle != null && _handle.Tier == AuthorityTier.Override && _handle.IsValid)
            {
                var result = _puppet?.Drive(_handle, PuppetController.ReadIntent());
                if (result != null)
                    Note($"{(result.Item2 ? "drive" : "DRIVE FAILED")}: {result.Item1.DisplayName} ({result.Item1.Mode})");
            }
        }

        // ---- Selection ------------------------------------------------------------------

        private static Vector2 GuiMousePosition() =>
            new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);

        /// <summary>Selects the registered enemy nearest the cursor in world space.</summary>
        private void PickUnderMouse()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 world = cam.ScreenToWorldPoint(Input.mousePosition);
            EnemyInstance best = null;
            float bestDistance = _pickRadius.Value;

            foreach (var enemy in EnemyBehavior.ActiveEnemies)
            {
                if (!enemy.IsAlive) continue;
                float d = Vector2.Distance(world, enemy.GameObject.transform.position);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = enemy;
            }

            if (best != null) Select(best);
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

            Note(_handle.Tier == AuthorityTier.Override
                ? $"took control ({_handle.Policy})"
                : $"only got {_handle.Tier} - someone else holds Override");
        }

        private void ReleaseControl()
        {
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
            _window = GUI.Window(GetInstanceID(), _window, DrawWindow, "Enemy Puppeteer");
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
            GUILayout.Label($"{enemies.Count} enemy instance(s) in scene - click one in the world, or pick below");

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
            GUILayout.Label("Arrow keys steer, using this enemy's own movement states:");
            foreach (Intent intent in new[] { Intent.Left, Intent.Right, Intent.Up, Intent.Down })
            {
                var bound = _puppet?.Preview(intent);
                GUILayout.Label($"   {intent,-6} {(bound != null ? $"{bound.DisplayName} ({bound.Mode})" : "- nothing bound -")}");
            }
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
