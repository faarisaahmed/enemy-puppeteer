using System;
using System.Collections.Generic;
using System.Linq;
using EnemyBehaviorApi;
using EnemyBehaviorApi.Authority;
using EnemyBehaviorApi.Runtime;

namespace EnemyPuppeteer
{
    /// <summary>
    /// Stops every enemy except the one being driven from reacting to Hornet.
    /// </summary>
    /// <remarks>
    /// The marker is a real, targetable Hornet standing in the room, so every enemy in
    /// earshot converges on it. That is correct behaviour and completely unusable: the
    /// fight becomes whatever the room happens to contain rather than the duel you set up.
    ///
    /// Rather than trying to hide Hornet - which is what the layer swap attempted, and which
    /// dropped her out of the world - this claims each bystander through the same public
    /// authority the puppet uses and suppresses its decisions. They keep running whatever
    /// state they are in and settle into idle, but cannot choose to chase or attack. The
    /// distinction matters: they are not blinded, they are prevented from acting, which is
    /// a thing the API can actually guarantee.
    ///
    /// Per-instance authority is what makes this legal. Holding Override on twenty enemies
    /// at once is exactly the case it was designed for.
    /// </remarks>
    public sealed class BystanderSuppressor : IDisposable
    {
        private readonly Dictionary<int, IEnemyHandle> _held = new Dictionary<int, IEnemyHandle>();
        private readonly Action<string> _log;
        private readonly string _ownerId;
        private readonly int _exemptInstanceId;
        private bool _disposed;

        /// <summary>How many bystanders are currently held.</summary>
        public int Count => _held.Count;

        /// <summary>Enemies another mod already controls, which cannot be suppressed.</summary>
        public int Contested { get; private set; }

        public BystanderSuppressor(string ownerId, EnemyInstance exempt, Action<string> log)
        {
            _ownerId = ownerId;
            _exemptInstanceId = exempt?.InstanceId ?? 0;
            _log = log ?? (_ => { });

            foreach (var enemy in EnemyBehavior.ActiveEnemies.ToList()) Suppress(enemy);

            // Anything that wanders in mid-fight has to be caught too, or the duel is
            // interrupted by whatever spawns next.
            EnemyBehavior.EnemyAppeared += Suppress;
            EnemyBehavior.EnemyGone += Forget;

            _log($"bystanders suppressed: {Count}" + (Contested > 0 ? $" ({Contested} already controlled elsewhere)" : ""));
        }

        private void Suppress(EnemyInstance enemy)
        {
            if (_disposed || enemy == null || !enemy.IsAlive) return;
            if (enemy.InstanceId == _exemptInstanceId || _held.ContainsKey(enemy.InstanceId)) return;

            var handle = EnemyBehavior.Claim(enemy, _ownerId, AuthorityTier.Override);
            if (handle == null) return;

            if (handle.Tier != AuthorityTier.Override)
            {
                // Someone else is driving this one. Leave it alone rather than half-holding
                // it, and say so - a bystander that keeps attacking is otherwise a mystery.
                Contested++;
                handle.Dispose();
                return;
            }

            // Decisions only. Suppressing everything would also veto death and hit
            // reactions, so a stray enemy could not be killed or even flinch.
            handle.Policy = OverridePolicy.SuppressDecisions;
            _held[enemy.InstanceId] = handle;
        }

        private void Forget(EnemyInstance enemy)
        {
            if (enemy == null || !_held.TryGetValue(enemy.InstanceId, out var handle)) return;
            handle.Dispose();
            _held.Remove(enemy.InstanceId);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            EnemyBehavior.EnemyAppeared -= Suppress;
            EnemyBehavior.EnemyGone -= Forget;

            foreach (var handle in _held.Values)
            {
                try { handle.Dispose(); }
                catch (Exception e) { _log($"could not release a bystander: {e.Message}"); }
            }
            _held.Clear();
        }
    }
}
