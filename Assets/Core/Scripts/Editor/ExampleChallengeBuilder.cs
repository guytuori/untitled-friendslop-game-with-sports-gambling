using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Builds the three example challenges Claude designed on 2026-10-21 (Challenge_16_09 "Slush Rush",
    /// Challenge_16_10 "Swing Shift" and Challenge_32_06 "Avalanche") from their glbs, the same way they'd be
    /// put together by hand:
    ///   - a root named after the challenge, with challenge_NN_start / challenge_NN_end at its origin (the
    ///     corner marker and the start / finish rows);
    ///   - each geometry glb (Assets/Maps/challenge_NN_MM*.glb, from TotalEditor3/challenge_NN_MM*.te3) at the
    ///     origin too, given its property with the real Set Object Property tool (Regular, Ice, Wall Climb,
    ///     Grind Rail);
    ///   - pole_4 instances moved, turned and stretched onto each pole's line, then set to Pole (bars and
    ///     upright spin poles for Pole Swing 2);
    ///   - a ChallengeZone with its own ChallengeDefinition (name and points; an existing one is reused, so
    ///     your edits to it survive a rebuild);
    /// then saves the prefab to Assets/Core/Prefabs/Challenges and a scene holding it to
    /// Assets/Core/Scenes/Challenges, and rebuilds the challenge lists.
    ///
    /// Friendslop > Challenges > Build Example Challenges. Safe to run again (it asks before overwriting).
    ///
    /// Challenge_32_07 "Shuttle Run" (2026-10-28, the first moving-platform challenge) is built on its own by
    /// Friendslop > Challenges > Build Challenge_32_07 (Moving Platforms), so building it never touches the
    /// other three. Its four platforms are MovingPlatform prefab instances (MovingPlatformBuilder), each with its
    /// own node positions, speed, pause and phase.
    ///
    /// Challenge_16_11 "Relay", Challenge_16_12 "Diamond Ferry" and Challenge_16_13 "Rush Hour" (2026-11-01) are three
    /// moving-platform 16s on the user's challenge_16_platforms.glb, built together by Friendslop > Challenges >
    /// Build Challenge_16_11-13 (Moving Platforms).
    ///
    /// Challenge_16_26 "Hand Over Hand" (2026-11-12, poles in the user's brick room) has its own menu too:
    /// Friendslop > Challenges > Build Challenge_16_26 (Shimmy).
    ///
    /// Once you're happy with them this file can be deleted - the prefabs and scenes don't need it.
    /// </summary>
    public static class ExampleChallengeBuilder
    {
        private const string GlbFolder = "Assets/Maps";
        private const string PrefabFolder = "Assets/Core/Prefabs/Challenges";
        private const string SceneFolder = "Assets/Core/Scenes/Challenges";
        private const string DefinitionFolder = "Assets/Core/Data/Challenges";
        private const string PoleGlb = "pole_4";
        private const string RingGlb = "grind_rail_small_ring";
        private const string SpikeGlb = "spikes";

        private sealed class Piece
        {
            public string Glb;
            public MapObjectPropertyType Type;
            public Piece(string glb, MapObjectPropertyType type) { Glb = glb; Type = type; }
        }

        private sealed class PoleLine
        {
            public string Name;
            public Vector3 From, To; // centre line, Total Editor metres (x, y, z)
            public PoleLine(string name, Vector3 from, Vector3 to) { Name = name; From = from; To = to; }
        }

        private sealed class PlatformPath
        {
            public string Name;
            public Vector3 A, B; // top centre at each end, Total Editor metres (x, y, z)
            public float Speed, Pause, Phase;
            /// <summary>Stood on edge (turned 90 degrees about x): a 4 m wide x 4 m tall wall, A / B its centre (2026-11-07 doors).</summary>
            public bool StandUp;
            public PlatformPath(string name, Vector3 a, Vector3 b, float speed, float pause, float phase, bool standUp = false)
            {
                Name = name; A = a; B = b; Speed = speed; Pause = pause; Phase = phase; StandUp = standUp;
            }
        }

        /// <summary>A dash panel glb (dash_panel_1x1 / dash_panel_2x2): its centre (Total Editor x, z; the top stays at y 2)
        /// and the way its arrows point, in degrees from -z (towards the finish) round towards +x.</summary>
        private sealed class PanelSpot
        {
            public string Glb;
            public float X, Z, Heading;
            public PanelSpot(string glb, float x, float z, float heading) { Glb = glb; X = x; Z = z; Heading = heading; }
        }

        /// <summary>A ring (grind_rail_small_ring, plain Regular collision - not a rail) stood upright across the z axis, centred at Centre (TE metres).</summary>
        private sealed class RingSpot
        {
            public string Name;
            public Vector3 Centre;
            public RingSpot(string name, Vector3 centre) { Name = name; Centre = centre; }
        }

        private sealed class RollerRun
        {
            public string Name;
            public Vector3 A, B;           // the roller's centre at each end, Total Editor metres (x, y, z)
            public Vector3[] Waypoints;    // in between, in order
            public float Speed, Pause, Phase;
            /// <summary>A long (2x1x1) roller going one way, A -> B, then starting over at A (tunnel to tunnel).</summary>
            public bool LongOneWay;
        }

        private sealed class Spec
        {
            public string Name;
            public int Size;
            public string Title;
            public int Points;
            public Piece[] Pieces;
            public PoleLine[] Poles;
            public PlatformPath[] Platforms = new PlatformPath[0];
            public RollerRun[] Rollers = new RollerRun[0];
            public PanelSpot[] Panels = new PanelSpot[0];
            /// <summary>Grind rails along straight lines (pole_4 stretched onto the line, then set to Grind Rail), TE metres.</summary>
            public PoleLine[] Rails = new PoleLine[0];
            public RingSpot[] Rings = new RingSpot[0];
            /// <summary>Spike challenge (2026-11-11): a SpikeField on the root turns the spikesup / spikesdown entities into spikes.</summary>
            public bool Spikes;
        }

        // ---- Spike challenges (2026-11-11): the user's glbs, all Regular (the spikes_down ones have a separate _ice
        // floor). The spikes come from the spikesup / spikesdown entities at runtime (SpikeField), so re-exporting a glb
        // with spikes moved needs no rebuild. ----

        // The spike mazes get a brick ceiling (challenge_NN_spikes_ceiling, 14 x 14 / 30 x 30 blocks, 3 blocks above the
        // floor - added 2026-11-11 after playing), so nobody can jump high enough to clear a spike at run speed.
        private static Spec SpikeSpec(string name, int size, string title, string glb, bool ice) => new Spec
        {
            Name = name, Size = size, Title = title, Points = size >= 32 ? 250 : 100,
            Pieces = ice
                ? new[] { new Piece(glb, MapObjectPropertyType.Regular), new Piece(glb + "_ice", MapObjectPropertyType.Ice) }
                : new[] { new Piece(glb, MapObjectPropertyType.Regular), new Piece($"challenge_{size}_spikes_ceiling", MapObjectPropertyType.Regular) },
            Poles = new PoleLine[0],
            Spikes = true,
        };

        private static Spec[] SpikeChallenges => new[]
        {
            // Spike mazes: keep to the turned (safe) floor tiles - or go 1.5x run speed and run straight over them.
            SpikeSpec("Challenge_16_22", 16, "Pins and Needles", "challenge_16_spikes_1", false),
            SpikeSpec("Challenge_16_23", 16, "Tread Lightly", "challenge_16_spikes_2", false),
            SpikeSpec("Challenge_16_24", 16, "Minefield", "challenge_16_spikes_3", false),
            SpikeSpec("Challenge_32_15", 32, "Bed of Nails", "challenge_32_spikes_1", false),
            SpikeSpec("Challenge_32_16", 32, "D\u00E9j\u00E0 Vu", "challenge_32_spikes_2", false), // starts like 32.15, then doesn't
            // Spikes from the ceiling over ice: keep running, round the slippery turns or jump the pits.
            SpikeSpec("Challenge_16_25", 16, "Ice Pick", "challenge_16_spikes_down_1", true),
            SpikeSpec("Challenge_32_17", 32, "Damocles", "challenge_32_spikes_down_1", true),
        };

        [MenuItem("Friendslop/Challenges/Build Challenge_16_22-25, 32_15-17 (Spikes)")]
        private static void BuildSpikeChallenges() => BuildSpecs(SpikeChallenges, "Build Spike Challenges");

        // ---- Challenge_32_09 "Head-On" / Challenge_32_10 "Tailgate" (2026-11-01) ----
        // Both on the user's challenge_32_roller_2.glb (bricks + black blocks, all Regular): a 4 m brick walkway (top y 2)
        // that snakes between 16 m walls from the start (z 64) to the finish (z 0) - five straight runs:
        //   1. left, x 6-10, from z 59 up to z 7          (long)
        //   2. top, z 8-12, from x 5 across to x 35         (short)
        //   3. middle, x 30-34, from z 7 down to z 59       (long)
        //   4. bottom, z 54-58, from x 29 across to x 59    (short)
        //   5. right, x 54-58, from z 59 up to z 5          (long)
        // Each run ends in a black 2 x 2 x 4 m block, the size of a long (2x1x1) roller, sitting across the walkway
        // (centres below). The rollers come out of one block, roll the run at a steady speed and disappear into the
        // block at the other end, then start over from the first (MovingPlatform one-way - nothing is spawned or
        // destroyed). Long runs carry two rollers half a run apart, short runs one; the speeds differ per run (13,
        // 10, 10.4, 12, 10.8 m/s - all faster than the player runs, so the ones from behind catch up). The runs cross
        // at the four corners: the speeds and phases were searched so rollers meet there as little as possible
        // (the top-middle corner can't be avoided entirely - about one brief clip every 7-8 s there; the others are
        // rare corner grazes or never). "Tailgate" is "Head-On" run backwards in time, so it clips the same.
        // Roller centres are at y 3 (on the walkway).

        private static RollerRun Tunnel(string name, float ax, float az, float bx, float bz, float speed, float phase) =>
            new RollerRun
            {
                Name = name, A = new Vector3(ax, 3f, az), B = new Vector3(bx, 3f, bz), Waypoints = new Vector3[0],
                Speed = speed, Pause = 0f, Phase = phase, LongOneWay = true,
            };

        // ---- Challenge_16_14 - 16_17 (2026-11-01): mini roller challenges on the user's challenge_16_roller_1/2/3.glb ----

        /// <summary>
        /// Rollers for a rib layout like Challenge_32_08's (4 m ribs with 45-degree ramps up to y 6 at each end, the
        /// 4 m roller rolling ramp to ramp), shifted <paramref name="shiftX"/> metres in x. The 16s' ribs run x 4-28
        /// (ramps x 4-8 and 24-28), i.e. 32_08's x 20-44 shifted by -16.
        /// </summary>
        private static RollerRun[] MakeRibRollers(float[] ribZ, float[] speed, float[] phase, float shiftX)
        {
            const float flatY = 4f, rampY = 6f;
            var runs = new RollerRun[ribZ.Length];
            for (int i = 0; i < ribZ.Length; i++)
            {
                float z = ribZ[i];
                runs[i] = new RollerRun
                {
                    Name = $"roller {i + 1}",
                    A = new Vector3(22.83f + shiftX, rampY, z),
                    Waypoints = new[] { new Vector3(24.83f + shiftX, flatY, z), new Vector3(39.17f + shiftX, flatY, z) },
                    B = new Vector3(41.17f + shiftX, rampY, z),
                    Speed = speed[i], Pause = 0f, Phase = phase[i],
                };
            }
            return runs;
        }

        /// <summary>
        /// "Speed Bumps" (challenge_16_roller_1): a mini Steamroller - a 4 m spine (x 14-18) from the start (z 32) to the
        /// finish (z 0), crossed by three ribs (z centres 24, 16, 8), a roller on each, getting faster down the course.
        /// </summary>
        private static readonly Spec SpeedBumpsChallenge = new Spec
        {
            Name = "Challenge_16_14", Size = 16, Title = "Speed Bumps", Points = 100,
            Pieces = new[] { new Piece("challenge_16_roller_1", MapObjectPropertyType.Regular) },
            Poles = new PoleLine[0],
            Rollers = MakeRibRollers(new[] { 24f, 16f, 8f }, new[] { 6.4f, 8.8f, 11.2f }, new[] { 0f, 0.35f, 0.7f }, -16f),
        };

        /// <summary>"Washboard" (challenge_16_roller_3): the same with five ribs (z centres 28, 22, 16, 10, 4).</summary>
        private static readonly Spec WashboardChallenge = new Spec
        {
            Name = "Challenge_16_15", Size = 16, Title = "Washboard", Points = 100,
            Pieces = new[] { new Piece("challenge_16_roller_3", MapObjectPropertyType.Regular) },
            Poles = new PoleLine[0],
            Rollers = MakeRibRollers(new[] { 28f, 22f, 16f, 10f, 4f }, new[] { 6.4f, 7.6f, 8.8f, 10f, 11.2f },
                new[] { 0f, 0.35f, 0.7f, 0.15f, 0.5f }, -16f),
        };

        // challenge_16_roller_2: a mini Head-On / Tailgate - one 4 m walkway (x 14-18, top y 2) from the start (z 32) to
        // the finish (z 0), with a black 2 x 2 x 4 m block (a long roller's size) at each end, under a brick cap: centres
        // (16, 3, 3) by the finish and (16, 3, 29) by the start. One long roller, tunnel to tunnel, 11 m/s.

        /// <summary>"Fender Bender": the roller comes at the player (finish end -> start end).</summary>
        private static readonly Spec FenderBenderChallenge = new Spec
        {
            Name = "Challenge_16_16", Size = 16, Title = "Fender Bender", Points = 100,
            Pieces = new[] { new Piece("challenge_16_roller_2", MapObjectPropertyType.Regular) },
            Poles = new PoleLine[0],
            Rollers = new[] { Tunnel("roller", 16f, 3f, 16f, 29f, 11f, 0f) },
        };

        /// <summary>"Bumper to Bumper": the roller comes from behind (start end -> finish end).</summary>
        private static readonly Spec BumperToBumperChallenge = new Spec
        {
            Name = "Challenge_16_17", Size = 16, Title = "Bumper to Bumper", Points = 100,
            Pieces = new[] { new Piece("challenge_16_roller_2", MapObjectPropertyType.Regular) },
            Poles = new PoleLine[0],
            Rollers = new[] { Tunnel("roller", 16f, 29f, 16f, 3f, 11f, 0f) },
        };

        /// <summary>Head-On: every roller rolls towards the player.</summary>
        private static readonly Spec HeadOnChallenge = new Spec
        {
            Name = "Challenge_32_09", Size = 32, Title = "Head-On", Points = 250,
            Pieces = new[] { new Piece("challenge_32_roller_2", MapObjectPropertyType.Regular) },
            Poles = new PoleLine[0],
            Rollers = new[]
            {
                Tunnel("run 1 (left) roller 1", 8f, 7f, 8f, 59f, 13f, 0f),
                Tunnel("run 1 (left) roller 2", 8f, 7f, 8f, 59f, 13f, 0.5f),
                Tunnel("run 2 (top) roller", 35f, 10f, 5f, 10f, 10f, 0.32f),
                Tunnel("run 3 (middle) roller 1", 32f, 59f, 32f, 7f, 10.4f, 0.94f),
                Tunnel("run 3 (middle) roller 2", 32f, 59f, 32f, 7f, 10.4f, 0.44f),
                Tunnel("run 4 (bottom) roller", 59f, 56f, 29f, 56f, 12f, 0f),
                Tunnel("run 5 (right) roller 1", 56f, 5f, 56f, 59f, 10.8f, 0f),
                Tunnel("run 5 (right) roller 2", 56f, 5f, 56f, 59f, 10.8f, 0.5f),
            },
        };

        /// <summary>Tailgate: every roller rolls the way the player goes, so they come from behind.</summary>
        private static readonly Spec TailgateChallenge = new Spec
        {
            Name = "Challenge_32_10", Size = 32, Title = "Tailgate", Points = 250,
            Pieces = new[] { new Piece("challenge_32_roller_2", MapObjectPropertyType.Regular) },
            Poles = new PoleLine[0],
            Rollers = new[]
            {
                Tunnel("run 1 (left) roller 1", 8f, 59f, 8f, 7f, 13f, 0f),
                Tunnel("run 1 (left) roller 2", 8f, 59f, 8f, 7f, 13f, 0.5f),
                Tunnel("run 2 (top) roller", 5f, 10f, 35f, 10f, 10f, 0.68f),
                Tunnel("run 3 (middle) roller 1", 32f, 7f, 32f, 59f, 10.4f, 0.06f),
                Tunnel("run 3 (middle) roller 2", 32f, 7f, 32f, 59f, 10.4f, 0.56f),
                Tunnel("run 4 (bottom) roller", 29f, 56f, 59f, 56f, 12f, 0f),
                Tunnel("run 5 (right) roller 1", 56f, 59f, 56f, 5f, 10.8f, 0f),
                Tunnel("run 5 (right) roller 2", 56f, 59f, 56f, 5f, 10.8f, 0.5f),
            },
        };

        /// <summary>
        /// Challenge_32_08 "Steamroller" (2026-10-30), from the user's challenge_32_roller_1.glb (bricks + steel
        /// railings, all Regular): a 4 m spine runs from the start (z 64) to the finish (z 0), crossed by seven
        /// 4 m-wide ribs (z centres 56, 48 ... 8) that end in 45-degree ramps (x 20-24 and 40-44, up to y 6). A roller
        /// (4 m cylinder) rolls each rib from ramp to ramp, across the spine, and touching one kills you - so the
        /// run is timing your dash past each one.
        /// Each roller's centre path (TE metres): up the left ramp at (22.83, 6) -> where it leaves the ramp
        /// (24.83, 4) -> along the floor -> (39.17, 4) -> up the right ramp to (41.17, 6), 20 m each way. With
        /// easing it slows into each ramp and is fastest crossing the spine. Speeds rise from 6.4 to 11.2 m/s down the
        /// course (60% up on the first tuning, at the user's request), with staggered phases.
        /// </summary>
        private static readonly Spec RollerChallenge = new Spec
        {
            Name = "Challenge_32_08", Size = 32, Title = "Steamroller", Points = 250,
            Pieces = new[]
            {
                new Piece("challenge_32_roller_1", MapObjectPropertyType.Regular),
            },
            Poles = new PoleLine[0],
            Rollers = MakeRollers(),
        };

        private static RollerRun[] MakeRollers()
        {
            float[] ribZ = { 56f, 48f, 40f, 32f, 24f, 16f, 8f };          // first one met first (the start is at z 64)
            float[] speed = { 6.4f, 7.2f, 8f, 8.8f, 9.6f, 10.4f, 11.2f };   // 2026-10-30: 60% faster than the first 4 - 7
            float[] phase = { 0f, 0.35f, 0.7f, 0.15f, 0.5f, 0.85f, 0.3f };
            const float flatY = 4f, rampY = 6f;                              // centre height on the floor / at the turn
            const float rampX = 22.83f, footX = 24.83f;                      // left ramp turn / where it meets the floor
            const float footX2 = 39.17f, rampX2 = 41.17f;                    // right side
            var runs = new RollerRun[ribZ.Length];
            for (int i = 0; i < ribZ.Length; i++)
            {
                float z = ribZ[i];
                runs[i] = new RollerRun
                {
                    Name = $"roller {i + 1}",
                    A = new Vector3(rampX, rampY, z),
                    Waypoints = new[] { new Vector3(footX, flatY, z), new Vector3(footX2, flatY, z) },
                    B = new Vector3(rampX2, rampY, z),
                    Speed = speed[i], Pause = 0f, Phase = phase[i],
                };
            }
            return runs;
        }

        /// <summary>
        /// Challenge_32_07 "Shuttle Run": a 42 m gap (z 10..52) between the start apron and the landing, crossed
        /// on four moving platforms (4 x 4 m, top at y 2 = rim level):
        ///   1. a ferry from the apron's edge out over the gap (z 50 -> 40);
        ///   2. a sweeper going side to side (x 22 <-> 42) at z 34 - hop on as it passes the middle;
        ///   3. another sweeper at z 26, going the other way;
        ///   4. a ferry (z 20 -> 12) that docks against the landing (z 0..10).
        /// The jumps between them are 2-4 m, so the challenge is the timing, not the distance. Platforms 1 and 4
        /// dock flush with the apron / landing edge, so you can walk on and off.
        /// </summary>
        private static readonly Spec MovingPlatformChallenge = new Spec
        {
            Name = "Challenge_32_07", Size = 32, Title = "Shuttle Run", Points = 250,
            Pieces = new[]
            {
                new Piece("challenge_32_07", MapObjectPropertyType.Regular),
            },
            Poles = new PoleLine[0],
            Platforms = new[]
            {
                new PlatformPath("platform 1 (ferry)", new Vector3(32f, 2f, 50f), new Vector3(32f, 2f, 40f), 2.5f, 1f, 0f),
                new PlatformPath("platform 2 (sweeper)", new Vector3(22f, 2f, 34f), new Vector3(42f, 2f, 34f), 3.5f, 0.5f, 0f),
                new PlatformPath("platform 3 (sweeper)", new Vector3(42f, 2f, 26f), new Vector3(22f, 2f, 26f), 3.5f, 0.5f, 0.25f),
                new PlatformPath("platform 4 (ferry)", new Vector3(32f, 2f, 20f), new Vector3(32f, 2f, 12f), 2.5f, 1f, 0.5f),
            },
        };

        // ---- Challenge_16_11 - 16_13 (2026-11-01): three moving-platform 16s on challenge_16_platforms.glb ----
        // The glb is just the two docks (sidewalk, top y 2): the start x 12-20, z 30-32 and the finish x 12-20, z 0-2,
        // with a 28 m gap between. Platforms are the 4 x 4 m moving platform, top at y 2 (flush with the docks); the
        // positions are its top centre (Total Editor metres). Node A is where each one starts (the user's squares).
        // Nodes that meet a dock put the platform flush against it (z 4 / z 28); platforms that meet each other
        // touch edge to edge (z 14 + z 18, or x 14 + x 18), so you can step across.

        /// <summary>
        /// "Relay": two lanes (x 14 and x 18) of two ferries each. In a lane the two ferries meet in the middle (z 14 /
        /// z 18) and part again, to the docks (z 28 / z 4); the right lane does the opposite of the left one, so one
        /// lane is always docked while the other is meeting in the middle.
        /// </summary>
        private static readonly Spec RelayChallenge = new Spec
        {
            Name = "Challenge_16_11", Size = 16, Title = "Relay", Points = 100,
            Pieces = new[] { new Piece("challenge_16_platforms", MapObjectPropertyType.Regular) },
            Poles = new PoleLine[0],
            Platforms = new[]
            {
                new PlatformPath("left lane, start half", new Vector3(14f, 2f, 28f), new Vector3(14f, 2f, 18f), 2.5f, 1f, 0f),
                new PlatformPath("left lane, finish half", new Vector3(14f, 2f, 4f), new Vector3(14f, 2f, 14f), 2.5f, 1f, 0f),
                new PlatformPath("right lane, start half", new Vector3(18f, 2f, 18f), new Vector3(18f, 2f, 28f), 2.5f, 1f, 0f),
                new PlatformPath("right lane, finish half", new Vector3(18f, 2f, 14f), new Vector3(18f, 2f, 4f), 2.5f, 1f, 0f),
            },
        };

        /// <summary>
        /// "Diamond Ferry": four ferries on the sides of a diamond. Each leaves a dock (x 14 or 18, flush at z 28 / z 4)
        /// on a diagonal out to the side (x 2 or x 30), where it meets the one from the other dock (z 18 / z 14). The
        /// left pair meets at the side while the right pair is docked, and the other way round.
        /// </summary>
        private static readonly Spec DiamondChallenge = new Spec
        {
            Name = "Challenge_16_12", Size = 16, Title = "Diamond Ferry", Points = 100,
            Pieces = new[] { new Piece("challenge_16_platforms", MapObjectPropertyType.Regular) },
            Poles = new PoleLine[0],
            Platforms = new[]
            {
                new PlatformPath("left, start side", new Vector3(14f, 2f, 28f), new Vector3(2f, 2f, 18f), 3f, 1f, 0f),
                new PlatformPath("left, finish side", new Vector3(14f, 2f, 4f), new Vector3(2f, 2f, 14f), 3f, 1f, 0f),
                new PlatformPath("right, start side", new Vector3(30f, 2f, 18f), new Vector3(18f, 2f, 28f), 3f, 1f, 0f),
                new PlatformPath("right, finish side", new Vector3(30f, 2f, 14f), new Vector3(18f, 2f, 4f), 3f, 1f, 0f),
            },
        };

        /// <summary>
        /// "Rush Hour": four rows of sweepers going side to side (x 4 to 28) across the gap, evenly spaced (z 26, 19.33,
        /// 12.67, 6 - 2 m from each dock, 2.67 m between rows). They start staggered like the user's squares - the row
        /// by the start at the left end, the next a third of the way across, then two thirds, the last at the right
        /// end, all heading right - so neighbouring rows line up for a moment near the turns, and you hop forward then.
        /// Phases are of the whole there-and-back cycle (13 s), worked out with the easing so they sit exactly there.
        /// </summary>
        private static readonly Spec RowsChallenge = new Spec
        {
            Name = "Challenge_16_13", Size = 16, Title = "Rush Hour", Points = 100,
            Pieces = new[] { new Piece("challenge_16_platforms", MapObjectPropertyType.Regular) },
            Poles = new PoleLine[0],
            Platforms = new[]
            {
                new PlatformPath("row 1 (by the start)", new Vector3(4f, 2f, 26f), new Vector3(28f, 2f, 26f), 4f, 0.5f, 0f),
                new PlatformPath("row 2", new Vector3(4f, 2f, 58f / 3f), new Vector3(28f, 2f, 58f / 3f), 4f, 0.5f, 0.2171f),
                new PlatformPath("row 3", new Vector3(4f, 2f, 38f / 3f), new Vector3(28f, 2f, 38f / 3f), 4f, 0.5f, 0.3214f),
                new PlatformPath("row 4 (by the finish)", new Vector3(4f, 2f, 6f), new Vector3(28f, 2f, 6f), 4f, 0.5f, 0.5f),
            },
        };

        // ---- 2026-11-07: dash panel challenges (16_19, 16_20, 32_11 - 32_13) and alternating poles (16_21, 32_14) ----
        // Ground glbs written by Claude (Claude outputs/challenge_generator/design_1107.py); dash panels, poles, rails, rings
        // and doors placed here. Player numbers used: run 9 m/s, dash panel 13.5 m/s (1.5x), jump 17.3 m/s up (5 m), gravity
        // 30 -> a running jump carries ~10.4 m on the flat, a dash jump ~15.6 m.

        /// <summary>Doors for "Door Dash": each side is two moving platforms stood on edge, one above the other (4 m wide,
        /// y 2..10). Closed they meet at x 15; open they're at x 7 / 23. Speed 3, pause 1 (a 6 s cycle).</summary>
        private static PlatformPath[] Doors(string set, float z, float phase)
        {
            var doors = new List<PlatformPath>();
            foreach ((string side, float closed, float open) in new[] { ("left", 13f, 7f), ("right", 17f, 23f) })
            {
                foreach ((string level, float y) in new[] { ("bottom", 4f), ("top", 8f) })
                {
                    doors.Add(new PlatformPath($"{set} door {side} {level}", new Vector3(closed, y, z), new Vector3(open, y, z), 3f, 1f, phase, standUp: true));
                }
            }
            return doors.ToArray();
        }

        /// <summary>
        /// "Door Dash": a dash panel on the start pad, then a gap only a dash jump clears (pad edge z 26, landing pad from
        /// z 14), with two sets of sliding doors in the way at z 21 and z 16. The back set runs 0.4 of a cycle behind the
        /// front one: about a 1.7 s window every 6 s to go through both.
        /// </summary>
        private static readonly Spec DoorDashChallenge = new Spec
        {
            Name = "Challenge_16_19", Size = 16, Title = "Door Dash", Points = 100,
            Pieces = new[] { new Piece("challenge_16_19", MapObjectPropertyType.Regular) },
            Poles = new PoleLine[0],
            Panels = new[] { new PanelSpot("dash_panel_1x1", 15f, 29f, 0f) },
            Platforms = Concat(Doors("front", 21f, 0f), Doors("back", 16f, 0.4f)),
        };

        /// <summary>
        /// "Dash &amp; Swing": a dash panel on the start pad; the dash jump reaches bar 1 (10 m out, y 8) - a running jump's
        /// hands are 3 m too low there - then bars 2 and 3 every 6 m, and a swing off bar 3 over the finish onto the rim.
        /// </summary>
        private static readonly Spec DashSwingChallenge = new Spec
        {
            Name = "Challenge_16_20", Size = 16, Title = "Dash & Swing", Points = 100,
            Pieces = new[] { new Piece("challenge_16_20", MapObjectPropertyType.Regular) },
            Panels = new[] { new PanelSpot("dash_panel_1x1", 15f, 29f, 0f) },
            Poles = new[]
            {
                new PoleLine("bar 1", new Vector3(11f, 8f, 16f), new Vector3(19f, 8f, 16f)),
                new PoleLine("bar 2", new Vector3(11f, 8f, 10f), new Vector3(19f, 8f, 10f)),
                new PoleLine("bar 3", new Vector3(11f, 8f, 4f), new Vector3(19f, 8f, 4f)),
            },
        };

        /// <summary>
        /// "Island Hopper": seven 2x2 dash panels floating over the pit, about 15 m apart centre to centre, each turned to
        /// point at the next (not on the grid), so it's a chain of angled dash jumps: land, get turned, jump again. A dash
        /// jump from anywhere between a panel's back edge and a little past its middle lands on the next one.
        /// </summary>
        private static readonly Spec IslandHopperChallenge = new Spec
        {
            Name = "Challenge_32_11", Size = 32, Title = "Island Hopper", Points = 250,
            Pieces = new[] { new Piece("challenge_32_11", MapObjectPropertyType.Regular) },
            Poles = new PoleLine[0],
            Panels = new[]
            {
                new PanelSpot("dash_panel_2x2", 16f, 55f, -28.3f),
                new PanelSpot("dash_panel_2x2", 9f, 42f, 72.2f),
                new PanelSpot("dash_panel_2x2", 23f, 37.5f, 103.1f),
                new PanelSpot("dash_panel_2x2", 38f, 41f, 36.9f),
                new PanelSpot("dash_panel_2x2", 47f, 29f, -61.7f),
                new PanelSpot("dash_panel_2x2", 34f, 22f, -27.4f),
                new PanelSpot("dash_panel_2x2", 27f, 8.5f, 0f),
            },
        };

        /// <summary>
        /// "The Big Jump": three 1x1 dash panels (13.5 m/s) into an 8-block grind rail that ramps up 15 degrees (z 50 -> 34.5,
        /// y 2 -> 6.1). Grind Rail 2 has no top speed, so a dash entry leaves the top at ~23 m/s; jumping at the very top
        /// flies ~40 m (apex ~15 m up) through three upright rings on the arc and lands on the rim ~5 m past the finish
        /// (jumping up to ~2 m early still makes it). Riding off the top without jumping drops short into the pit.
        /// </summary>
        private static readonly Spec BigJumpChallenge = new Spec
        {
            Name = "Challenge_32_12", Size = 32, Title = "The Big Jump", Points = 250,
            Pieces = new[] { new Piece("challenge_32_12", MapObjectPropertyType.Regular) },
            Poles = new PoleLine[0],
            Panels = new[]
            {
                new PanelSpot("dash_panel_1x1", 31f, 55f, 0f),
                new PanelSpot("dash_panel_1x1", 31f, 53f, 0f),
                new PanelSpot("dash_panel_1x1", 31f, 51f, 0f),
            },
            Rails = new[] { new PoleLine("ramp rail", new Vector3(31f, 2f, 50f), new Vector3(31f, 6.141f, 34.545f)) },
            Rings = new[]
            {
                new RingSpot("ring 1", new Vector3(31f, 13.37f, 26.92f)),
                new RingSpot("ring 2", new Vector3(31f, 16.01f, 16.53f)),
                new RingSpot("ring 3", new Vector3(31f, 12.52f, 6.14f)),
            },
        };

        /// <summary>
        /// "Split Fast Ice": two 2x2 dash panels shoot you onto an ice wishbone. Either arm (8 m wide, bending out 25 degrees
        /// and back, all on ice) goes to the finish; or jump the gap down the middle onto a grind rail (z 34 -> 22, y 2.9),
        /// off it onto four more dash panels (z 12..20) and an ice runway to the finish.
        /// </summary>
        private static readonly Spec SplitFastIceChallenge = new Spec
        {
            Name = "Challenge_32_13", Size = 32, Title = "Split Fast Ice", Points = 250,
            Pieces = new[]
            {
                new Piece("challenge_32_13", MapObjectPropertyType.Regular),
                new Piece("challenge_32_13_ice", MapObjectPropertyType.Ice),
            },
            Poles = new PoleLine[0],
            Panels = new[]
            {
                new PanelSpot("dash_panel_2x2", 30f, 54f, 0f),
                new PanelSpot("dash_panel_2x2", 34f, 54f, 0f),
                new PanelSpot("dash_panel_2x2", 30f, 18f, 0f),
                new PanelSpot("dash_panel_2x2", 34f, 18f, 0f),
                new PanelSpot("dash_panel_2x2", 30f, 14f, 0f),
                new PanelSpot("dash_panel_2x2", 34f, 14f, 0f),
            },
            Rails = new[] { new PoleLine("middle rail", new Vector3(32f, 2.9f, 34f), new Vector3(32f, 2.9f, 22f)) },
        };

        /// <summary>
        /// "Pole Position": upright, bar, upright, bar (Pole Swing 2 all the way). Uprights are 12 m tall (y 2..14) -
        /// shimmy to the height of the next bar before letting go; bars are at y 8. Upright -> bar 6.5 m, bar -> upright 8 m.
        /// </summary>
        private static readonly Spec PolePositionChallenge = new Spec
        {
            Name = "Challenge_16_21", Size = 16, Title = "Pole Position", Points = 100,
            Pieces = new[] { new Piece("challenge_16_21", MapObjectPropertyType.Regular) },
            Poles = new[]
            {
                new PoleLine("upright 1", new Vector3(13f, 2f, 25f), new Vector3(13f, 14f, 25f)),
                new PoleLine("bar 1", new Vector3(11f, 8f, 18.5f), new Vector3(21f, 8f, 18.5f)),
                new PoleLine("upright 2", new Vector3(19f, 2f, 10.5f), new Vector3(19f, 14f, 10.5f)),
                new PoleLine("bar 2", new Vector3(11f, 8f, 4f), new Vector3(21f, 8f, 4f)),
            },
        };

        /// <summary>
        /// "Swing Vote": upright, bar, upright, bar ... with a split. Bar 1 is 24 m long: shimmy left or right, then
        /// left (upright, bar at y 8, upright) or right (taller uprights and a bar at y 10.5 - climb before letting go).
        /// Both land on the 28 m bar 3 (shimmy back to the middle), then upright 4 and bar 4 over the finish.
        /// </summary>
        private static readonly Spec SwingVoteChallenge = new Spec
        {
            Name = "Challenge_32_14", Size = 32, Title = "Swing Vote", Points = 250,
            Pieces = new[] { new Piece("challenge_32_14", MapObjectPropertyType.Regular) },
            Poles = new[]
            {
                new PoleLine("upright 1", new Vector3(32f, 2f, 55f), new Vector3(32f, 14f, 55f)),
                new PoleLine("bar 1 (pick a side)", new Vector3(20f, 8f, 48.5f), new Vector3(44f, 8f, 48.5f)),
                new PoleLine("left upright 2", new Vector3(24f, 2f, 40.5f), new Vector3(24f, 14f, 40.5f)),
                new PoleLine("left bar 2", new Vector3(18f, 8f, 34f), new Vector3(30f, 8f, 34f)),
                new PoleLine("left upright 3", new Vector3(22f, 2f, 26f), new Vector3(22f, 14f, 26f)),
                new PoleLine("right upright 2", new Vector3(40f, 2f, 40.5f), new Vector3(40f, 16f, 40.5f)),
                new PoleLine("right bar 2 (high)", new Vector3(34f, 10.5f, 34f), new Vector3(46f, 10.5f, 34f)),
                new PoleLine("right upright 3", new Vector3(42f, 2f, 26f), new Vector3(42f, 16f, 26f)),
                new PoleLine("bar 3 (both sides)", new Vector3(18f, 8f, 19.5f), new Vector3(46f, 8f, 19.5f)),
                new PoleLine("upright 4", new Vector3(32f, 2f, 11.5f), new Vector3(32f, 14f, 11.5f)),
                new PoleLine("bar 4", new Vector3(26f, 8f, 5f), new Vector3(38f, 8f, 5f)),
            },
        };

        /// <summary>
        /// "Hand Over Hand" (2026-11-12): the user's brick room (challenge_16_shimmy_1) over the pit, all shimmying on Pole
        /// Swing 2 poles, laid out from the user's sketch. Up the outside pole, through the high hole in the front wall
        /// onto the first inside pole, down it and across onto the front bar, shimmy right, onto the right pole, down it
        /// and through the low hole in the inner wall onto the far right pole, up it onto the back bar, shimmy left,
        /// onto the last pole, down it and out through the exit hole onto the finish ledge (challenge_16_shimmy_1_ledge,
        /// Claude's, top flush with the rim). The exit hole is meant to be one block up (rim level to 4 m above): with
        /// it at layers 0-1 only 2 m of it is above the rim.
        ///
        /// Simulated with the CorePlayer values (spin 6-10 m/s):
        ///   - pole to pole through a hole: outside -> first inside ~21 degree release window (let go with hands 10-12 m);
        ///     right -> far right ~15-18 degrees (hands 1-2 m, through the 0-4 m hole);
        ///   - pole -> bar and bar -> pole are short hand-offs at the bar ends (bars stop 1.5 m short of the pole they
        ///     start from and 1 m short of the pole they lead to; a bar launch has no sideways push along the bar, so
        ///     the pole it leads to has to be within grab reach of the bar's end): let go from the pole with your feet
        ///     over the bar (hands ~2 m below it), or from the bar's end swinging past the pole;
        ///   - last pole -> exit: ~27-42 degrees, hands 2-4 m.
        /// Each pole's line keeps the 2 m swing (plus the body) off the walls where it matters, and each hole is on the
        /// line between the poles either side of it, so both spin directions have a way through.
        /// </summary>
        private static readonly Spec HandOverHandChallenge = new Spec
        {
            Name = "Challenge_16_26", Size = 16, Title = "Hand Over Hand", Points = 100,
            Pieces = new[]
            {
                new Piece("challenge_16_shimmy_1", MapObjectPropertyType.Regular),
                new Piece("challenge_16_shimmy_1_ledge", MapObjectPropertyType.Regular),
            },
            Poles = new[]
            {
                new PoleLine("1 outside pole (up)", new Vector3(4f, 0f, 27.6f), new Vector3(4f, 13f, 27.6f)),
                new PoleLine("2 first inside pole (down)", new Vector3(4f, 0f, 21.8f), new Vector3(4f, 14f, 21.8f)),
                new PoleLine("3 front bar (shimmy right)", new Vector3(5.5f, 8f, 21.8f), new Vector3(26.8f, 8f, 20.3f)),
                new PoleLine("4 right pole (down)", new Vector3(27.8f, 0f, 20.3f), new Vector3(27.8f, 10f, 20.3f)),
                new PoleLine("5 far right pole (up)", new Vector3(27.8f, 0f, 14.2f), new Vector3(27.8f, 11f, 14.2f)),
                new PoleLine("6 back bar (shimmy left)", new Vector3(26.3f, 9f, 14.2f), new Vector3(5.2f, 9f, 10.3f)),
                new PoleLine("7 last pole (down)", new Vector3(4.2f, 0f, 10.3f), new Vector3(4.2f, 11f, 10.3f)),
            },
        };

        private static T[] Concat<T>(T[] a, T[] b)
        {
            var all = new T[a.Length + b.Length];
            a.CopyTo(all, 0);
            b.CopyTo(all, a.Length);
            return all;
        }

        private static readonly Spec[] Specs =
        {
            new Spec
            {
                Name = "Challenge_16_09", Size = 16, Title = "Slush Rush", Points = 100,
                Pieces = new[]
                {
                    new Piece("challenge_16_09", MapObjectPropertyType.Regular),
                    new Piece("challenge_16_09_ice", MapObjectPropertyType.Ice),
                    new Piece("challenge_16_09_rails", MapObjectPropertyType.GrindRail),
                },
                Poles = new PoleLine[0],
            },
            new Spec
            {
                Name = "Challenge_16_10", Size = 16, Title = "Swing Shift", Points = 100,
                Pieces = new[]
                {
                    new Piece("challenge_16_10", MapObjectPropertyType.Regular),
                },
                Poles = new[]
                {
                    new PoleLine("bar", new Vector3(12f, 9f, 23f), new Vector3(20f, 9f, 23f)),
                    new PoleLine("spin pole 1", new Vector3(13.5f, 2f, 9f), new Vector3(13.5f, 14f, 9f)),
                    new PoleLine("spin pole 2", new Vector3(18f, 2f, 4.5f), new Vector3(18f, 14f, 4.5f)),
                },
            },
            new Spec
            {
                Name = "Challenge_32_06", Size = 32, Title = "Avalanche", Points = 250,
                Pieces = new[]
                {
                    new Piece("challenge_32_06", MapObjectPropertyType.Regular),
                    new Piece("challenge_32_06_climb", MapObjectPropertyType.WallClimb),
                    new Piece("challenge_32_06_ice", MapObjectPropertyType.Ice),
                    new Piece("challenge_32_06_rails", MapObjectPropertyType.GrindRail),
                },
                Poles = new[]
                {
                    new PoleLine("bar left", new Vector3(23f, 9f, 8f), new Vector3(31f, 9f, 8f)),
                    new PoleLine("bar right", new Vector3(33f, 9f, 8f), new Vector3(41f, 9f, 8f)),
                },
            },
        };

        [MenuItem("Friendslop/Challenges/Build Example Challenges")]
        private static void BuildAll() => BuildSpecs(Specs, "Build Example Challenges");

        [MenuItem("Friendslop/Challenges/Build Challenge_16_14-17 (Mini Rollers)")]
        private static void BuildMiniRollerChallenges() =>
            BuildSpecs(new[] { SpeedBumpsChallenge, WashboardChallenge, FenderBenderChallenge, BumperToBumperChallenge }, "Build Challenge_16_14-17");

        [MenuItem("Friendslop/Challenges/Build Challenge_32_09-10 (Tunnel Rollers)")]
        private static void BuildTunnelRollerChallenges() =>
            BuildSpecs(new[] { HeadOnChallenge, TailgateChallenge }, "Build Challenge_32_09-10");

        [MenuItem("Friendslop/Challenges/Build Challenge_32_08 (Rollers)")]
        private static void BuildRollerChallenge() =>
            BuildSpecs(new[] { RollerChallenge }, "Build Challenge_32_08");

        [MenuItem("Friendslop/Challenges/Build Challenge_16_11-13 (Moving Platforms)")]
        private static void BuildPlatform16Challenges() =>
            BuildSpecs(new[] { RelayChallenge, DiamondChallenge, RowsChallenge }, "Build Challenge_16_11-13");

        [MenuItem("Friendslop/Challenges/Build Challenge_16_19-20, 32_11-13 (Dash Panels)")]
        private static void BuildDashPanelChallenges() =>
            BuildSpecs(new[] { DoorDashChallenge, DashSwingChallenge, IslandHopperChallenge, BigJumpChallenge, SplitFastIceChallenge },
                "Build Dash Panel Challenges");

        [MenuItem("Friendslop/Challenges/Build Challenge_16_21, 32_14 (Alternating Poles)")]
        private static void BuildAlternatingPoleChallenges() =>
            BuildSpecs(new[] { PolePositionChallenge, SwingVoteChallenge }, "Build Alternating Pole Challenges");

        [MenuItem("Friendslop/Challenges/Build Challenge_16_26 (Shimmy)")]
        private static void BuildShimmyChallenge() =>
            BuildSpecs(new[] { HandOverHandChallenge }, "Build Challenge_16_26");

        [MenuItem("Friendslop/Challenges/Build Challenge_32_07 (Moving Platforms)")]
        private static void BuildMovingPlatformChallenge() =>
            BuildSpecs(new[] { MovingPlatformChallenge }, "Build Challenge_32_07");

        private static void BuildSpecs(Spec[] specs, string title)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            AssetDatabase.Refresh();
            var missing = new List<string>();
            foreach (Spec spec in specs)
            {
                foreach (string glb in GlbsFor(spec))
                {
                    if (AssetDatabase.LoadAssetAtPath<GameObject>(GlbPath(glb)) == null && !missing.Contains(glb)) missing.Add(glb);
                }
            }
            if (missing.Count > 0)
            {
                EditorUtility.DisplayDialog(title,
                    $"These glbs aren't in {GlbFolder} (or haven't imported yet):\n\n{string.Join("\n", missing)}", "OK");
                return;
            }

            var existing = new List<string>();
            foreach (Spec spec in specs)
            {
                if (File.Exists(PrefabPath(spec)) || File.Exists(ScenePath(spec))) existing.Add(spec.Name);
            }
            if (existing.Count > 0 && !EditorUtility.DisplayDialog(title,
                    $"This replaces the prefab and scene of:\n\n{string.Join("\n", existing)}\n\nTheir ChallengeDefinition assets are kept.", "Rebuild", "Cancel"))
            {
                return;
            }

            var report = new StringBuilder();
            foreach (Spec spec in specs)
            {
                try
                {
                    report.AppendLine(Build(spec));
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    report.AppendLine($"{spec.Name}: failed - {e.Message}");
                }
            }

            AssetDatabase.SaveAssets();
            report.AppendLine();
            report.AppendLine(MatchCatalogBuilder.Rebuild());
            Debug.Log($"[Friendslop] {title}:\n" + report);
            EditorUtility.DisplayDialog(title, report.ToString(), "OK");
        }

        private static IEnumerable<string> GlbsFor(Spec spec)
        {
            yield return $"challenge_{spec.Size}_start";
            yield return $"challenge_{spec.Size}_end";
            foreach (Piece piece in spec.Pieces) yield return piece.Glb;
            if (spec.Poles.Length > 0 || spec.Rails.Length > 0) yield return PoleGlb;
            foreach (PanelSpot panel in spec.Panels) yield return panel.Glb;
            if (spec.Rings.Length > 0) yield return RingGlb;
            if (spec.Platforms.Length > 0) yield return "moving_platform";
            if (spec.Rollers.Length > 0) yield return "roller";
            if (spec.Spikes) yield return SpikeGlb;
        }

        private static string GlbPath(string glb) => $"{GlbFolder}/{glb}.glb";
        private static string PrefabPath(Spec spec) => $"{PrefabFolder}/{spec.Name}.prefab";
        private static string ScenePath(Spec spec) => $"{SceneFolder}/{spec.Name}.unity";

        private static string Build(Spec spec)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject(spec.Name);
            int problems = 0;

            Place($"challenge_{spec.Size}_start", root);
            Place($"challenge_{spec.Size}_end", root);

            foreach (Piece piece in spec.Pieces)
            {
                GameObject obj = Place(piece.Glb, root);
                if (!MapObjectPropertySetup.ApplyProperty(obj, piece.Type)) problems++;
            }

            foreach (PoleLine line in spec.Poles)
            {
                GameObject pole = Place(PoleGlb, root);
                pole.name = line.Name;
                FitPole(pole, line.From, line.To);
                if (!MapObjectPropertySetup.ApplyProperty(pole, MapObjectPropertyType.Pole)) problems++;
            }

            foreach (PlatformPath path in spec.Platforms)
            {
                // Total Editor metres -> challenge space (the glb import mirrors x).
                Vector3 a = new Vector3(-path.A.x, path.A.y, path.A.z);
                Vector3 b = new Vector3(-path.B.x, path.B.y, path.B.z);
                MovingPlatform mover = MovingPlatformBuilder.Place(root.transform, path.Name, a, b, path.Speed, path.Pause, path.Phase);
                if (mover == null) { problems++; continue; }
                if (path.StandUp)
                {
                    // On edge: the 4 x 4 m top turns to face along z, centred on the node (the thickness goes behind it).
                    mover.PlatformTransform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(mover.PlatformTransform);
                }
            }

            foreach (RollerRun run in spec.Rollers)
            {
                // Total Editor metres -> challenge space (the glb import mirrors x).
                Vector3 a = new Vector3(-run.A.x, run.A.y, run.A.z);
                Vector3 b = new Vector3(-run.B.x, run.B.y, run.B.z);
                if (run.LongOneWay)
                {
                    if (RollerBuilder.PlaceLongOneWay(root.transform, run.Name, a, b, run.Speed, run.Phase) == null) problems++;
                    continue;
                }
                var mids = new Vector3[run.Waypoints.Length];
                for (int i = 0; i < mids.Length; i++) mids[i] = new Vector3(-run.Waypoints[i].x, run.Waypoints[i].y, run.Waypoints[i].z);
                if (RollerBuilder.Place(root.transform, run.Name, a, mids, b, run.Speed, run.Pause, run.Phase) == null) problems++;
            }

            int panelNumber = 0;
            foreach (PanelSpot spot in spec.Panels)
            {
                // Named dash_panel... so DashPanel finds it at runtime; solid like any floor.
                GameObject panel = Place(spot.Glb, root);
                panel.name = $"{spot.Glb} ({++panelNumber})";
                PlaceDashPanel(panel, spot);
                if (!MapObjectPropertySetup.ApplyProperty(panel, MapObjectPropertyType.Regular)) problems++;
            }

            foreach (PoleLine line in spec.Rails)
            {
                GameObject rail = Place(PoleGlb, root);
                rail.name = line.Name;
                FitPole(rail, line.From, line.To);
                if (!MapObjectPropertySetup.ApplyProperty(rail, MapObjectPropertyType.GrindRail)) problems++;
            }

            foreach (RingSpot spot in spec.Rings)
            {
                GameObject ring = Place(RingGlb, root);
                ring.name = spot.Name;
                ring.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // stood up, facing along z
                CentreOn(ring, new Vector3(-spot.Centre.x, spot.Centre.y, spot.Centre.z));
                if (!MapObjectPropertySetup.ApplyProperty(ring, MapObjectPropertyType.Regular)) problems++; // a hoop to fly through, not a rail
            }

            if (spec.Spikes)
            {
                var field = root.AddComponent<SpikeField>();
                var serializedField = new SerializedObject(field);
                serializedField.FindProperty("spikeModel").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(GlbPath(SpikeGlb));
                serializedField.ApplyModifiedPropertiesWithoutUndo();
            }

            AddZone(root, spec);

            GameObject prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(root, PrefabPath(spec), InteractionMode.AutomatedAction, out bool saved);
            if (!saved || prefab == null) return $"{spec.Name}: couldn't save {PrefabPath(spec)}";

            EditorSceneManager.SaveScene(scene, ScenePath(spec));
            return $"{spec.Name} \"{spec.Title}\": built ({spec.Pieces.Length} pieces, {spec.Poles.Length} poles, {spec.Platforms.Length} moving platforms, {spec.Rollers.Length} rollers, {spec.Panels.Length} dash panels, {spec.Rails.Length} rails, {spec.Rings.Length} rings)" +
                   (problems > 0 ? $" - {problems} piece(s) couldn't be set up, see the Console" : "");
        }

        /// <summary>A glb instance (still linked to the glb, so re-exports flow through) at the challenge's origin.</summary>
        private static GameObject Place(string glb, GameObject root)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(GlbPath(glb));
            if (asset == null) throw new FileNotFoundException($"Missing {GlbPath(glb)}");

            var obj = (GameObject)PrefabUtility.InstantiatePrefab(asset, root.transform);
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;
            return obj;
        }

        /// <summary>
        /// Lines a pole glb (an upright bar, long along its own Y) up with <paramref name="from"/> → <paramref name="to"/>
        /// (Total Editor metres; the glb import mirrors x, so x is negated): turned to point along the line, stretched
        /// along its length to match, centred on the line's middle.
        /// </summary>
        private static void FitPole(GameObject pole, Vector3 from, Vector3 to)
        {
            Vector3 a = new Vector3(-from.x, from.y, from.z);
            Vector3 b = new Vector3(-to.x, to.y, to.z);
            Vector3 along = b - a;
            float length = along.magnitude;

            MeshFilter filter = pole.GetComponentInChildren<MeshFilter>();
            Bounds bounds = filter.sharedMesh.bounds;

            Transform t = pole.transform;
            t.localRotation = Quaternion.FromToRotation(Vector3.up, along / length);
            t.localScale = new Vector3(1f, length / Mathf.Max(0.01f, bounds.size.y), 1f);
            t.localPosition = Vector3.zero;

            // The challenge root sits at the scene origin, so world and challenge space are the same here.
            Vector3 centreNow = filter.transform.TransformPoint(bounds.center);
            t.localPosition = (a + b) * 0.5f - centreNow;
        }

        /// <summary>
        /// Turns a dash panel about y so its arrows (the model's -z) point along <see cref="PanelSpot.Heading"/>, and moves
        /// it so its middle is at the spot (its top stays at y 2). Total Editor x is mirrored on import.
        /// </summary>
        private static void PlaceDashPanel(GameObject panel, PanelSpot spot)
        {
            float rad = spot.Heading * Mathf.Deg2Rad;
            Vector3 aim = new Vector3(-Mathf.Sin(rad), 0f, -Mathf.Cos(rad)); // TE (sin, -cos) with x mirrored
            float yaw = Mathf.Atan2(-aim.x, -aim.z) * Mathf.Rad2Deg;         // local -z onto aim
            panel.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            CentreOn(panel, new Vector3(-spot.X, float.NaN, spot.Z));
        }

        /// <summary>Moves <paramref name="obj"/> so its renderers' middle is at <paramref name="target"/> (challenge space; a NaN y keeps the height).</summary>
        private static void CentreOn(GameObject obj, Vector3 target)
        {
            Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            // The challenge root sits at the scene origin, so world and challenge space are the same here.
            Vector3 shift = target - bounds.center;
            if (float.IsNaN(target.y)) shift.y = 0f;
            obj.transform.localPosition += shift;
        }

        private static void AddZone(GameObject root, Spec spec)
        {
            string path = $"{DefinitionFolder}/{spec.Name}Definition.asset";
            var definition = AssetDatabase.LoadAssetAtPath<ChallengeDefinition>(path);
            if (definition == null)
            {
                if (!AssetDatabase.IsValidFolder(DefinitionFolder))
                {
                    Directory.CreateDirectory(DefinitionFolder);
                    AssetDatabase.Refresh();
                }
                definition = ScriptableObject.CreateInstance<ChallengeDefinition>();
                definition.challengeName = spec.Title;
                definition.basePoints = spec.Points;
                AssetDatabase.CreateAsset(definition, path);
            }

            var zone = root.AddComponent<ChallengeZone>();
            var serialized = new SerializedObject(zone);
            serialized.FindProperty("definition").objectReferenceValue = definition;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
