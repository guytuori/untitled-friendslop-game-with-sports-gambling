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
    /// Once you're happy with them this file can be deleted - the prefabs and scenes don't need it.
    /// </summary>
    public static class ExampleChallengeBuilder
    {
        private const string GlbFolder = "Assets/Maps";
        private const string PrefabFolder = "Assets/Core/Prefabs/Challenges";
        private const string SceneFolder = "Assets/Core/Scenes/Challenges";
        private const string DefinitionFolder = "Assets/Core/Data/Challenges";
        private const string PoleGlb = "pole_4";

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
            public PlatformPath(string name, Vector3 a, Vector3 b, float speed, float pause, float phase)
            {
                Name = name; A = a; B = b; Speed = speed; Pause = pause; Phase = phase;
            }
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
        }

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
            if (spec.Poles.Length > 0) yield return PoleGlb;
            if (spec.Platforms.Length > 0) yield return "moving_platform";
            if (spec.Rollers.Length > 0) yield return "roller";
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
                if (MovingPlatformBuilder.Place(root.transform, path.Name, a, b, path.Speed, path.Pause, path.Phase) == null) problems++;
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

            AddZone(root, spec);

            GameObject prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(root, PrefabPath(spec), InteractionMode.AutomatedAction, out bool saved);
            if (!saved || prefab == null) return $"{spec.Name}: couldn't save {PrefabPath(spec)}";

            EditorSceneManager.SaveScene(scene, ScenePath(spec));
            return $"{spec.Name} \"{spec.Title}\": built ({spec.Pieces.Length} pieces, {spec.Poles.Length} poles, {spec.Platforms.Length} moving platforms, {spec.Rollers.Length} rollers)" +
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
