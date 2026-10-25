using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Where each item's effect goes. Both run on the player's own machine (see LocalItemSlot). Using any item
    /// plays its use sound. Implemented (2026-10-25): Small / Medium / Big Points (ItemDefinition.points), the
    /// 1-Up (PlayerScore.GrantOneUp - a life, or the death shield with unlimited lives) and the Double / Triple /
    /// Quadruple Jump Boots (2 / 3 / 4 jumps in total while they last), Rocket Shoes / Super Rocket Shoes (+50% /
    /// +100% run speed), Spring Shoes / Super Spring Shoes (+50% / +100% jump height) and the Jetpack (hold Jump in
    /// the air, burning stamina - see JetpackAbility). The rest only log for now.
    ///
    /// The planned effects, for whoever fills these in:
    ///   - Double / Triple / Quadruple Jump Boots: 2 / 3 / 4 jumps in total - the jump off the ground plus 1 / 2 / 3
    ///     in the air (DoubleJumpAddon.MaxDoubleJumps, put back to the player's own value when they run out).
    ///   - Rocket Shoes / Super Rocket Shoes: run speed x1.5 / x2 (CoreMovement.RunSpeedMultiplier).
    ///   - Spring Shoes / Super Spring Shoes: jump height x1.5 / x2 (CoreMovement.JumpHeightMultiplier).
    ///   - Small / Medium / Big Points: +100 / +300 / +500 (PlayerScore.AddScore).
    ///   - 1-Up: a life if lives are limited, otherwise the next death costs 0 points.
    ///   - Teleporter: to the start of a random challenge this player hasn't completed, or to the end point if
    ///     they've done them all.
    ///   - Jetpack: hold Jump in the air to fly, on stamina (JetpackAbility).
    ///   - Glider (glide after jumping), Magnet Boots (no slipping),
    ///     Big Arms (more pole launch power), Slippery Boots (every floor slippery, double challenge and
    ///     pickup points), Gravity Boots (triple gravity, double challenge and pickup points).
    /// </summary>
    public static class ItemEffects
    {
        // Jump boots: the addon they changed and its value before, to put back when they run out.
        private static DoubleJumpAddon s_BootedAddon;
        private static int s_BaseAirJumps;

        // Rocket / Spring Shoes: the movement they changed, to put back to 1 when they run out.
        private static CoreMovement s_BoostedMotor;

        /// <summary>Run speed multiplier for the Rocket Shoes, or 0 for other items.</summary>
        public static float RunSpeedFor(ItemId id)
        {
            switch (id)
            {
                case ItemId.RocketShoes: return 1.5f;
                case ItemId.SuperRocketShoes: return 2f;
                default: return 0f;
            }
        }

        /// <summary>Jump height multiplier for the Spring Shoes, or 0 for other items.</summary>
        public static float JumpHeightFor(ItemId id)
        {
            switch (id)
            {
                case ItemId.SpringShoes: return 1.5f;
                case ItemId.SuperSpringShoes: return 2f;
                default: return 0f;
            }
        }

        /// <summary>Jumps in the air the jump boots allow (on top of the jump off the ground), or -1 for other items.</summary>
        public static int AirJumpsFor(ItemId id)
        {
            switch (id)
            {
                case ItemId.DoubleJumpBoots: return 1;
                case ItemId.TripleJumpBoots: return 2;
                case ItemId.QuadrupleJumpBoots: return 3;
                default: return -1;
            }
        }

        /// <summary>The player used <paramref name="item"/> (timed items start their timer now).</summary>
        public static void Used(ItemDefinition item, CorePlayerManager player)
        {
            if (item == null) return;
            string who = player != null ? player.name : "The local player";

            // Every item's own sound (ItemDefinition.useSound - points.mp3, item_1up.mp3, ...), heard by this player.
            if (item.useSound != null) AudioVolumeService.PlayOneShot(item.useSound, AudioCategory.SoundEffects, Vector3.zero);

            PlayerScore score = player != null ? player.GetComponent<PlayerScore>() : null;
            switch (item.id)
            {
                case ItemId.SmallPoints:
                case ItemId.MediumPoints:
                case ItemId.BigPoints:
                    if (score != null && item.points != 0) score.AddScore(item.points);
                    Debug.Log($"[Items] {who} used {item.displayName}: +{item.points} points.", player);
                    return;

                case ItemId.OneUp:
                    if (score == null) return;
                    bool life = score.GrantOneUp();
                    Debug.Log(life
                        ? $"[Items] {who} used {item.displayName}: +1 life ({score.Lives} now)."
                        : $"[Items] {who} used {item.displayName}: lives are unlimited, so the next death costs no points.", player);
                    return;

                case ItemId.DoubleJumpBoots:
                case ItemId.TripleJumpBoots:
                case ItemId.QuadrupleJumpBoots:
                    StartJumpBoots(item, player, who);
                    return;

                case ItemId.RocketShoes:
                case ItemId.SuperRocketShoes:
                case ItemId.SpringShoes:
                case ItemId.SuperSpringShoes:
                    StartBoost(item, player, who);
                    return;

                case ItemId.Jetpack:
                    JetpackAbility jetpack = JetpackAbility.For(player);
                    if (jetpack == null)
                    {
                        Debug.LogWarning($"[Items] {who} used {item.displayName}, but the player has no CoreMovement.", player);
                        return;
                    }
                    jetpack.Equip(player);
                    Debug.Log($"[Items] {who} used {item.displayName}: hold Jump in the air to fly for {item.durationSeconds:0.#} s.", player);
                    return;
            }

            string how = item.IsTimed ? $"timed, {item.durationSeconds:0.#} s" : "single use";
            Debug.Log($"[Items] {who} used {item.displayName} ({how}). Its effect isn't implemented yet.", player);
        }

        /// <summary>A timed item ran out (or the round/player went away while it was running).</summary>
        public static void Expired(ItemDefinition item, CorePlayerManager player)
        {
            if (item == null) return;
            string who = player != null ? player.name : "The local player";

            if (AirJumpsFor(item.id) >= 0) EndJumpBoots();
            if (RunSpeedFor(item.id) > 0f || JumpHeightFor(item.id) > 0f) EndBoost();
            if (item.id == ItemId.Jetpack && player != null && player.CoreMovement != null)
            {
                var jetpack = player.CoreMovement.GetComponent<JetpackAbility>();
                if (jetpack != null) jetpack.Unequip();
            }

            Debug.Log($"[Items] {who}'s {item.displayName} ran out.", player);
        }

        private static void StartJumpBoots(ItemDefinition item, CorePlayerManager player, string who)
        {
            DoubleJumpAddon addon = player != null ? player.GetComponentInChildren<DoubleJumpAddon>(true) : null;
            if (addon == null)
            {
                Debug.LogWarning($"[Items] {who} used {item.displayName}, but the player has no DoubleJumpAddon.", player);
                return;
            }

            EndJumpBoots(); // boots still on from before: start from the player's own value
            s_BootedAddon = addon;
            s_BaseAirJumps = addon.MaxDoubleJumps;

            int airJumps = AirJumpsFor(item.id);
            // Already unlimited (-1) or more than the boots give: leave it alone.
            if (s_BaseAirJumps >= 0 && s_BaseAirJumps < airJumps) addon.MaxDoubleJumps = airJumps;
            Debug.Log($"[Items] {who} used {item.displayName}: {airJumps + 1} jumps for {item.durationSeconds:0.#} s.", player);
        }

        private static void StartBoost(ItemDefinition item, CorePlayerManager player, string who)
        {
            CoreMovement motor = player != null ? player.CoreMovement : null;
            if (motor == null)
            {
                Debug.LogWarning($"[Items] {who} used {item.displayName}, but the player has no CoreMovement.", player);
                return;
            }

            EndBoost(); // shoes still on from before: start from normal
            s_BoostedMotor = motor;

            float run = RunSpeedFor(item.id);
            float jump = JumpHeightFor(item.id);
            if (run > 0f) motor.RunSpeedMultiplier = run;
            if (jump > 0f) motor.JumpHeightMultiplier = jump;
            Debug.Log(run > 0f
                ? $"[Items] {who} used {item.displayName}: run speed x{run:0.##} for {item.durationSeconds:0.#} s."
                : $"[Items] {who} used {item.displayName}: jump height x{jump:0.##} for {item.durationSeconds:0.#} s.", player);
        }

        private static void EndBoost()
        {
            if (s_BoostedMotor != null)
            {
                s_BoostedMotor.RunSpeedMultiplier = 1f;
                s_BoostedMotor.JumpHeightMultiplier = 1f;
            }
            s_BoostedMotor = null;
        }

        private static void EndJumpBoots()
        {
            if (s_BootedAddon != null) s_BootedAddon.MaxDoubleJumps = s_BaseAirJumps;
            s_BootedAddon = null;
        }
    }
}
