using System;
using System.Collections.Generic;
using System.Linq;
using EnemyBehaviorApi.Authority;
using EnemyBehaviorApi.Schema;
using UnityEngine;

namespace EnemyPuppeteer
{
    /// <summary>What the player is asking the puppet to do this frame.</summary>
    public enum Intent
    {
        None,
        Left,
        Right,
        Up,
        Down,
    }

    /// <summary>
    /// Translates arrow keys into whichever of the enemy's own movement behaviours best
    /// matches the direction pressed.
    /// </summary>
    /// <remarks>
    /// This is not a velocity puppet, and it cannot be. The Override tier fires state
    /// transitions - it does not write position - so the honest ceiling is "make the enemy
    /// do a thing it already knows how to do, now". Pressing left on an enemy with no
    /// leftward behaviour does nothing, which is the correct outcome and is exactly the
    /// constraint worth feeling while testing.
    /// </remarks>
    public sealed class PuppetController
    {
        /// <summary>
        /// How long to wait before re-firing a held direction. Movement states usually
        /// finish in well under a second and fall back to the enemy's own idle, so a held
        /// key has to re-fire to read as continuous movement.
        /// </summary>
        private const float RepeatSeconds = 0.35f;

        private readonly Dictionary<Intent, float> _nextFireAt = new Dictionary<Intent, float>();

        /// <summary>Movement descriptors grouped by the direction they best serve.</summary>
        public Dictionary<Intent, List<MovementDescriptor>> Bindings { get; } =
            new Dictionary<Intent, List<MovementDescriptor>>();

        public PuppetController(EnemyProfile profile)
        {
            foreach (Intent intent in Enum.GetValues(typeof(Intent)))
                Bindings[intent] = new List<MovementDescriptor>();

            if (profile == null) return;

            foreach (var move in profile.Movements.OrderByDescending(m => m.Confidence))
            {
                foreach (var intent in IntentsFor(move))
                    Bindings[intent].Add(move);
            }
        }

        /// <summary>
        /// Which directions a movement behaviour can serve.
        /// </summary>
        /// <remarks>
        /// Horizontal is deliberately generous. Nothing in the schema records which way a
        /// state sends the enemy - most of them go wherever it is already facing - so a
        /// walk is offered for both left and right and the enemy's own facing logic decides.
        /// Being honest about that beats inventing a direction the data does not contain.
        /// </remarks>
        private static IEnumerable<Intent> IntentsFor(MovementDescriptor move)
        {
            switch (move.Mode)
            {
                case MovementMode.Walk:
                case MovementMode.Chase:
                case MovementMode.Dash:
                    yield return Intent.Left;
                    yield return Intent.Right;
                    break;

                case MovementMode.Jump:
                    yield return Intent.Up;
                    break;

                case MovementMode.Fly:
                    yield return Intent.Up;
                    yield return Intent.Left;
                    yield return Intent.Right;
                    break;

                case MovementMode.Teleport:
                    yield return Intent.Up;
                    break;

                case MovementMode.Idle:
                    yield return Intent.Down;
                    break;

                case MovementMode.Turn:
                    yield return Intent.Left;
                    yield return Intent.Right;
                    break;
            }
        }

        /// <summary>The behaviour a direction will fire, or null if the enemy has none.</summary>
        public MovementDescriptor Preview(Intent intent) =>
            Bindings.TryGetValue(intent, out var list) && list.Count > 0 ? list[0] : null;

        /// <summary>
        /// Fires the movement bound to <paramref name="intent"/>, rate-limited so a held key
        /// does not spam transitions every frame.
        /// </summary>
        /// <returns>The descriptor fired and whether the API accepted it, or null if nothing fired.</returns>
        public Tuple<MovementDescriptor, bool> Drive(IEnemyHandle handle, Intent intent)
        {
            if (handle == null || !handle.IsValid || intent == Intent.None) return null;

            var move = Preview(intent);
            if (move == null) return null;

            _nextFireAt.TryGetValue(intent, out float next);
            if (Time.time < next) return null;
            _nextFireAt[intent] = Time.time + RepeatSeconds;

            return Tuple.Create(move, handle.Fire(move.Id));
        }

        /// <summary>Reads the arrow keys. Vertical wins so a diagonal does not silently drop the jump.</summary>
        public static Intent ReadIntent()
        {
            if (Input.GetKey(KeyCode.UpArrow)) return Intent.Up;
            if (Input.GetKey(KeyCode.DownArrow)) return Intent.Down;
            if (Input.GetKey(KeyCode.LeftArrow)) return Intent.Left;
            if (Input.GetKey(KeyCode.RightArrow)) return Intent.Right;
            return Intent.None;
        }
    }
}
