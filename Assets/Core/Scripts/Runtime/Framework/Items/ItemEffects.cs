using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Where each item's effect goes. None are implemented yet (2026-10-21): using an item just logs it, and a
    /// timed item logs again when it runs out. Both run on the player's own machine (see LocalItemSlot).
    ///
    /// The planned effects, for whoever fills these in:
    ///   - Double / Triple / Quadruple Jump Boots: 1 / 2 / 3 extra air jumps (DoubleJumpAddon.maxDoubleJumps).
    ///   - Speed / Super Speed Boots: higher run speed.
    ///   - Small / Medium / Big Points: +100 / +300 / +500 (PlayerScore.AddScore).
    ///   - 1-Up: a life if lives are limited, otherwise the next death costs 0 points.
    ///   - Teleport: to the start of a random challenge this player hasn't completed, or to the end point if
    ///     they've done them all.
    ///   - Jetpack, Spring Shoes (high jump), Glider (glide after jumping), Magnet Boots (no slipping),
    ///     Stronger Arms (more pole launch power), Slippery Boots (every floor slippery, double challenge and
    ///     pickup points), Gravity Boots (triple gravity, double challenge and pickup points).
    /// </summary>
    public static class ItemEffects
    {
        /// <summary>The player used <paramref name="item"/> (timed items start their timer now).</summary>
        public static void Used(ItemDefinition item, CorePlayerManager player)
        {
            if (item == null) return;
            string who = player != null ? player.name : "The local player";
            string how = item.IsTimed ? $"timed, {item.durationSeconds:0.#} s" : "single use";
            Debug.Log($"[Items] {who} used {item.displayName} ({how}). Its effect isn't implemented yet.", player);
        }

        /// <summary>A timed item ran out (or the round/player went away while it was running).</summary>
        public static void Expired(ItemDefinition item, CorePlayerManager player)
        {
            if (item == null) return;
            string who = player != null ? player.name : "The local player";
            Debug.Log($"[Items] {who}'s {item.displayName} ran out.", player);
        }
    }
}
