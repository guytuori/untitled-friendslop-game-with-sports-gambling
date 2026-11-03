using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The Teleporter item (added 2026-11-03): puts the player straight in front of a random challenge they haven't
    /// completed yet - on the cell just before its start row, centred, facing in (ChallengeCourse.TryGetEntrance) -
    /// with the player and camera turned to face it.
    ///
    ///   - Only safe spots count (<see cref="IsSafe"/>): not under or about to be under rising water (Ninja Pirate
    ///     Tower - RisingWaterManager), not in water (WaterVolume), and not blocked by any map gimmick that registers
    ///     a check in <see cref="Hazards"/> (e.g. a future Invasion Mothership one).
    ///   - With no challenge left (all done, or every remaining one unsafe), it goes in front of the end of the
    ///     map's end point instead: one step off its tile (not touching it), preferring the side facing the spawn
    ///     point, facing the tile.
    ///   - The challenge the player is running right now, and one they're already standing at, are only picked if
    ///     there's nothing else.
    ///   - Practice (Practice Map / Practice Challenges): any challenge or the end point, at random, completed or
    ///     not - so the player can see where a teleport leaves them.
    ///
    /// The use sound (item_teleporter) is played by ItemEffects like every item's. Anything the player is holding on
    /// to (rail, wall, pole) is let go first, the way dying does it.
    /// </summary>
    public static class Teleporter
    {
        /// <summary>
        /// Map gimmicks that make places unsafe to teleport to: return true for a spot (a player's feet) that isn't.
        /// Register in OnEnable, remove in OnDisable.
        /// </summary>
        public static readonly List<Func<Vector3, bool>> Hazards = new List<Func<Vector3, bool>>();

        private const float EndPointGap = 1.2f;    // from the end tile's edge to the player's body
        private const float PlayerRadius = 0.3f;
        private const float PlayerHeight = 1.8f;
        private const float AlreadyThereDistance = 4f;
        private const float GroundSearchUp = 2.5f;
        private const float GroundSearchDown = 4f;
        private const float FeetLift = 0.05f;

        private struct Target
        {
            public Vector3 Feet;
            public Vector3 Facing;
            public string Name;
        }

        /// <summary>Uses the item. False (and a log) if there was nowhere to go.</summary>
        public static bool Use(CorePlayerManager player, string who)
        {
            CoreMovement movement = player != null ? player.CoreMovement : null;
            if (movement == null)
            {
                Debug.LogWarning($"[Items] {who} used the Teleporter, but the player has no CoreMovement.", player);
                return false;
            }

            if (!TryPickTarget(movement.transform.position, out Target target))
            {
                Debug.LogWarning($"[Items] {who} used the Teleporter, but there's no safe challenge or end point to go to.", player);
                return false;
            }

            TeleportTo(player, target.Feet, target.Facing);
            Debug.Log($"[Items] {who} used the Teleporter: now in front of {target.Name}.", player);
            return true;
        }

        // =====================================================================================
        // Where to
        // =====================================================================================

        private static bool TryPickTarget(Vector3 from, out Target target)
        {
            bool practice = PracticeMode.IsActive;
            var good = new List<Target>();     // what we want
            var fallback = new List<Target>(); // the running challenge / where the player already is

            foreach (ChallengeCourse course in ChallengeCourse.All)
            {
                if (course == null || !course.isActiveAndEnabled) continue;
                if (!practice && course.State == ChallengeCourse.LocalState.Done) continue;
                if (!TryGetChallengeSpot(course, false, out Vector3 feet, out Vector3 facing)) continue;

                string name = course.Definition != null && !string.IsNullOrEmpty(course.Definition.challengeName)
                    ? $"the challenge \"{course.Definition.challengeName}\"" : $"the challenge {course.name}";
                var t = new Target { Feet = feet, Facing = facing, Name = name };

                bool running = course.State == ChallengeCourse.LocalState.Running;
                bool alreadyThere = Flat(feet - from).magnitude < AlreadyThereDistance;
                if (!practice && (running || alreadyThere)) fallback.Add(t);
                else good.Add(t);
            }

            // Practice: the end point is one of the possible spots too. Otherwise it's only for when no challenge is left.
            if (practice || good.Count == 0)
            {
                if (TryGetEndPointTarget(out Target end))
                {
                    if (practice) good.Add(end);
                    else
                    {
                        target = end;
                        return true;
                    }
                }
            }

            List<Target> pool = good.Count > 0 ? good : fallback;
            if (pool.Count == 0)
            {
                target = default;
                return false;
            }
            target = pool[UnityEngine.Random.Range(0, pool.Count)];
            return true;
        }

        /// <summary>
        /// The spot in front of a challenge (ChallengeCourse.TryGetEntrance) on the floor, and the direction into it,
        /// if it's safe (<see cref="IsSafe"/>). Also where LocalRespawnTracker brings back a player who died in that
        /// challenge (2026-11-03). <paramref name="requireGround"/>: false if no floor is found there (otherwise the
        /// spot is used as it is).
        /// </summary>
        public static bool TryGetChallengeSpot(ChallengeCourse course, bool requireGround, out Vector3 feet, out Vector3 facing)
        {
            feet = default;
            facing = default;
            if (course == null || !course.TryGetEntrance(out Vector3 entrance, out facing)) return false;
            if (!TryFindGround(entrance, out feet))
            {
                if (requireGround) return false;
                feet = entrance + Vector3.up * FeetLift;
            }
            return IsSafe(feet);
        }

        /// <summary>One step off the end point's tile, on solid ground, clear of walls, facing it - preferring the side towards the spawn point.</summary>
        private static bool TryGetEndPointTarget(out Target target)
        {
            target = default;
            IReadOnlyList<PlayerEndPoint> ends = PlayerEndPoint.All;
            if (ends == null || ends.Count == 0) return false;

            Vector3 towardsSpawn = Vector3.zero;
            GameManager gameManager = GameManager.Instance;
            PlayerEndPoint first = null;
            foreach (PlayerEndPoint e in ends) { if (e != null) { first = e; break; } }
            if (first == null) return false;
            if (gameManager != null && gameManager.TryGetSpawnPoint(out Vector3 spawn, out _))
            {
                towardsSpawn = Flat(spawn - first.Center);
            }

            var sides = new List<(PlayerEndPoint End, Vector3 Dir, float Score)>();
            foreach (PlayerEndPoint end in ends)
            {
                if (end == null) continue;
                foreach (Vector3 dir in new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left })
                {
                    float score = towardsSpawn.sqrMagnitude > 0.01f ? Vector3.Dot(dir, towardsSpawn.normalized) : 0f;
                    sides.Add((end, dir, score));
                }
            }
            sides.Sort((a, b) => b.Score.CompareTo(a.Score));

            foreach ((PlayerEndPoint end, Vector3 dir, float _) in sides)
            {
                Bounds tile = end.TileBounds;
                float half = Mathf.Abs(dir.x) > 0.5f ? tile.extents.x : tile.extents.z;
                Vector3 feet = new Vector3(tile.center.x, tile.max.y, tile.center.z) + dir * (half + EndPointGap + PlayerRadius);
                if (!TryFindGround(feet, out feet)) continue;
                if (!IsClear(feet) || !IsSafe(feet)) continue;

                target = new Target { Feet = feet, Facing = -dir, Name = "the end point" };
                return true;
            }
            return false;
        }

        /// <summary>True if a player could stand at <paramref name="feet"/>: no rising water there (now or soon), no water, no map hazard.</summary>
        public static bool IsSafe(Vector3 feet)
        {
            if (RisingWaterManager.IsUnderwaterSoon(feet)) return false;
            if (WaterVolume.IsInWater(feet + Vector3.up * 0.1f)) return false;
            foreach (Func<Vector3, bool> hazard in Hazards)
            {
                if (hazard != null && hazard(feet)) return false;
            }
            return true;
        }

        /// <summary>The floor nearest in height to <paramref name="feet"/> (within a few metres above or below), if there is one.</summary>
        private static bool TryFindGround(Vector3 feet, out Vector3 ground)
        {
            ground = feet;
            Vector3 start = feet + Vector3.up * GroundSearchUp;
            RaycastHit[] hits = Physics.RaycastAll(start, Vector3.down, GroundSearchUp + GroundSearchDown, WorldMask(), QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool found = false;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null || hit.collider.GetComponentInParent<CorePlayerManager>() != null) continue;
                if (hit.normal.y < 0.5f) continue; // not a floor
                float off = Mathf.Abs(hit.point.y - feet.y); // nearest to the expected height, not the first from above
                if (off < best)
                {
                    best = off;
                    ground = hit.point + Vector3.up * FeetLift;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>Room for the player's body at <paramref name="feet"/>.</summary>
        private static bool IsClear(Vector3 feet)
        {
            Vector3 bottom = feet + Vector3.up * (PlayerRadius + 0.1f);
            Vector3 top = feet + Vector3.up * (PlayerHeight - PlayerRadius);
            return !Physics.CheckCapsule(bottom, top, PlayerRadius, WorldMask(), QueryTriggerInteraction.Ignore);
        }

        private static int WorldMask()
        {
            int mask = Physics.DefaultRaycastLayers;
            foreach (string name in new[] { "Player", "PlayerHitBox" })
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0) mask &= ~(1 << layer);
            }
            return mask;
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // =====================================================================================
        // Going there
        // =====================================================================================

        private static void TeleportTo(CorePlayerManager player, Vector3 feet, Vector3 facing)
        {
            CoreMovement movement = player.CoreMovement;
            LetGoOfEverything(player, movement);

            Quaternion rotation = Quaternion.LookRotation(facing, Vector3.up);
            movement.transform.rotation = rotation;
            if (movement.RotationTransform != null) movement.RotationTransform.rotation = rotation;
            movement.SetPosition(feet);
            movement.ResetMovementForces();
            if (player.CoreCamera != null) player.CoreCamera.ResetLook(rotation.eulerAngles.y);
        }

        /// <summary>
        /// Off any rail, wall, pole or beam: each movement ability is told the player died and came straight back,
        /// which is how they all let go when a player dies (they'd otherwise pull the player back to where they were).
        /// </summary>
        private static void LetGoOfEverything(CorePlayerManager player, CoreMovement movement)
        {
            PlayerLifeState state = player.PlayerState != null ? player.PlayerState.LifeState : PlayerLifeState.Respawned;
            if (state == PlayerLifeState.Eliminated) state = PlayerLifeState.Respawned;

            foreach (IMovementAbility ability in player.GetComponentsInChildren<IMovementAbility>(true))
            {
                if (!(ability is IPlayerAddon addon)) continue;
                addon.OnLifeStateChanged(state, PlayerLifeState.Eliminated);
                addon.OnLifeStateChanged(PlayerLifeState.Eliminated, state);
            }

            movement.SetGrindState(false);
            movement.SetClimbState(false);
            movement.SetPoleGrabState(false);
            movement.RotationOverride = null;
        }
    }
}
