using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The step between the gameplay scene loading and the round starting - both from Start Match in the lobby
    /// and from Round Results into the next round. The course is already built (map plus this round's
    /// challenges), and the camera flies over it while everyone finishes loading:
    ///   - bottom right: "Loading", "Loading.", "Loading..", "Loading..." cycling, until this machine is
    ///     ready (map built, round spawned, own player spawned) - often before the first frame;
    ///   - then, bottom middle: "Press Any Key to Start", flashing like the title screen's prompt;
    ///   - after pressing: "Waiting for other players" and "k / n ready";
    ///   - when everyone has pressed, the camera goes back to the player and the HUD comes back for the
    ///     "3, 2, 1, PARKOUR!" countdown (the master client switches RoundTimer from Intro to Countdown, so
    ///     it's the same moment for everyone; see RoundAnnouncer), then controls unlock and the timers start.
    ///
    /// Added at runtime by <see cref="RoundTimer"/> (no scene setup); it removes itself once the round is on.
    /// Practice modes skip it (RoundTimer starts them straight in Playing), and so does a scene whose
    /// RoundTimer has Play Intro unticked.
    ///
    /// Meanwhile: every player's avatar is hidden, this machine's player can't move (input lock "Intro"), the
    /// HUD is hidden except its scoreboard - so everyone sees the scores they start the round on and the
    /// round's TARGET, in the usual place - (the Esc menu still works) and the Cinemachine brain is paused so
    /// this can move the camera. Esc / Start (the menu buttons) don't count as "any key".
    ///
    /// The flyover itself comes from <see cref="FlyoverFor"/>, per map:
    ///   - Dinosaur Zoo: <see cref="FinishToSpawn"/> - starts on the finish, then backs away towards the
    ///     player spawn while turning (the dome comes into view first), arriving with a half turn done, and
    ///     keeps turning there;
    ///   - every other map, for now: the placeholder <see cref="SpinAtSpawn"/> (sit at a spawn point, turn slowly).
    /// </summary>
    public class MatchIntro : MonoBehaviour
    {
        public const string InputLock = "Intro";
        private const float DotSeconds = 0.4f;
        private const float PromptFlashSeconds = 0.5f;
        private const float PressGraceSeconds = 0.35f;   // presses this soon after the prompt appears are ignored
        private const float FadeOutSeconds = 0.35f;

        /// <summary>A camera path over the course: where the camera is <paramref name="t"/> seconds in.</summary>
        public interface IFlyover
        {
            bool TryEvaluate(float t, out Vector3 position, out Quaternion rotation);
        }

        /// <summary>True while an intro is showing on this machine.</summary>
        public static bool IsActive { get; private set; }

        private RoundTimer m_Round;
        private IFlyover m_Flyover;
        private float m_StartTime;

        private UIDocument m_Document;
        private VisualElement m_Root;
        private Label m_Loading, m_Prompt, m_Ready;
        private bool m_LocallyReady, m_Pressed, m_Ending;
        private float m_ReadySince, m_EndStarted;

        private readonly Dictionary<VisualElement, StyleEnum<DisplayStyle>> m_HiddenHud = new Dictionary<VisualElement, StyleEnum<DisplayStyle>>();
        private readonly HashSet<Renderer> m_HiddenRenderers = new HashSet<Renderer>();
        private CinemachineBrain m_PausedBrain;
        private CorePlayerManager m_LockedPlayer;

        /// <summary>Starts the intro for this round (called by RoundTimer when it spawns in its Intro phase).</summary>
        public static void Begin(RoundTimer round)
        {
            if (round == null || round.GetComponent<MatchIntro>() != null) return;
            var intro = round.gameObject.AddComponent<MatchIntro>();
            intro.m_Round = round;
        }

        private void Start()
        {
            IsActive = true;
            m_StartTime = Time.unscaledTime;
            BuildOverlay();
        }

        private void OnDestroy()
        {
            Restore();
            if (m_Document != null) Destroy(m_Document.gameObject);
            IsActive = false;
        }

        // =====================================================================================
        // Every frame
        // =====================================================================================

        private void Update()
        {
            if (m_Round == null) { Destroy(this); return; }

            bool intro = m_Round.IsSpawned && m_Round.Phase == RoundTimer.RoundPhase.Intro;
            if (!intro && !m_Ending)
            {
                // The round has started (or was never in Intro): hand everything back and fade the overlay out.
                m_Ending = true;
                m_EndStarted = Time.unscaledTime;
                Restore();
            }

            if (m_Ending)
            {
                float k = Mathf.Clamp01((Time.unscaledTime - m_EndStarted) / FadeOutSeconds);
                if (m_Root != null) m_Root.style.opacity = 1f - k;
                if (k >= 1f) Destroy(this);
                return;
            }

            HideWhatShouldntShow();
            UpdateOverlay();
        }

        private void LateUpdate()
        {
            if (m_Ending) return;

            Camera camera = Camera.main;
            if (camera == null) return;

            // Pause Cinemachine (the player's camera may only appear partway through) and drive the camera here.
            CinemachineBrain brain = camera.GetComponent<CinemachineBrain>();
            if (brain != null && brain.enabled)
            {
                brain.enabled = false;
                m_PausedBrain = brain;
            }

            m_Flyover ??= FlyoverFor(MatchLayout.Current);
            if (m_Flyover != null && m_Flyover.TryEvaluate(Time.unscaledTime - m_StartTime, out Vector3 position, out Quaternion rotation))
            {
                camera.transform.SetPositionAndRotation(position, rotation);
            }
            else
            {
                m_Flyover = null; // nothing to look at yet (map still building) - try again next frame
            }
        }

        /// <summary>Hides every avatar, the HUD's own elements, and locks this machine's player.</summary>
        private void HideWhatShouldntShow()
        {
            foreach (CorePlayerManager player in NetworkPlayers.All)
            {
                if (player == null) continue;
                foreach (Renderer r in player.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.forceRenderingOff) continue;
                    r.forceRenderingOff = true;
                    m_HiddenRenderers.Add(r);
                }
            }

            CorePlayerManager local = NetworkPlayers.Local;
            if (local != null && local != m_LockedPlayer)
            {
                local.SetInputLock(InputLock, true);
                m_LockedPlayer = local;
            }

            if (local != null && local.TryGetComponent(out CoreHUD hud) && hud.TryGetComponent(out UIDocument doc) && doc.rootVisualElement != null)
            {
                VisualElement root = doc.rootVisualElement;
                HideAllBut(root, root.Q<VisualElement>("scoreboard-container"));
            }
        }

        /// <summary>
        /// Hides every child of <paramref name="parent"/> except the Esc menu and <paramref name="keep"/> - and
        /// for a child that holds <paramref name="keep"/> further down, everything in it except that branch.
        /// </summary>
        private void HideAllBut(VisualElement parent, VisualElement keep)
        {
            foreach (VisualElement child in parent.Children())
            {
                if (child == keep || child.name == "ingame-menu") continue;
                if (keep != null && IsAncestor(child, keep))
                {
                    HideAllBut(child, keep);
                    continue;
                }
                if (m_HiddenHud.ContainsKey(child)) continue;
                // Already hidden (e.g. by its UXML) - leave it alone. Hiding it and then "restoring" its code-set
                // display value, which is unset for a UXML-hidden element, would show it (see CoreHUD's round banner).
                if (child.resolvedStyle.display == DisplayStyle.None && child.style.display.keyword == StyleKeyword.Null) continue;
                m_HiddenHud[child] = child.style.display;
                child.style.display = DisplayStyle.None;
            }
        }

        private static bool IsAncestor(VisualElement ancestor, VisualElement element)
        {
            for (VisualElement e = element?.parent; e != null; e = e.parent)
            {
                if (e == ancestor) return true;
            }
            return false;
        }

        /// <summary>Gives back everything <see cref="HideWhatShouldntShow"/> and the camera took over.</summary>
        private void Restore()
        {
            foreach (Renderer r in m_HiddenRenderers)
            {
                if (r != null) r.forceRenderingOff = false;
            }
            m_HiddenRenderers.Clear();

            foreach (KeyValuePair<VisualElement, StyleEnum<DisplayStyle>> pair in m_HiddenHud)
            {
                if (pair.Key != null) pair.Key.style.display = pair.Value;
            }
            m_HiddenHud.Clear();

            if (m_LockedPlayer != null) m_LockedPlayer.SetInputLock(InputLock, false);
            m_LockedPlayer = null;

            if (m_PausedBrain != null) m_PausedBrain.enabled = true;
            m_PausedBrain = null;
        }

        // =====================================================================================
        // Overlay
        // =====================================================================================

        private void BuildOverlay()
        {
            PanelSettings panel = null;
            foreach (UIDocument doc in FindObjectsByType<UIDocument>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (doc.panelSettings != null) { panel = doc.panelSettings; break; }
            }
            if (panel == null)
            {
                Debug.LogWarning("[MatchIntro] No UI panel settings found in the scene - the intro will have no text.", this);
                return;
            }

            var go = new GameObject("MatchIntroOverlay");
            go.SetActive(false);
            m_Document = go.AddComponent<UIDocument>();
            m_Document.panelSettings = panel;
            m_Document.sortingOrder = 50f;
            go.SetActive(true);

            m_Root = m_Document.rootVisualElement;
            m_Root.style.flexGrow = 1;
            m_Root.pickingMode = PickingMode.Ignore;

            m_Loading = new Label(Loc.T("Loading"));
            m_Loading.style.position = Position.Absolute;
            m_Loading.style.right = 40;
            m_Loading.style.bottom = 30;
            m_Loading.style.fontSize = 20;
            m_Loading.style.color = Color.white;
            m_Loading.style.unityTextAlign = TextAnchor.MiddleLeft;
            m_Loading.style.width = 160; // fixed, so the word doesn't shift as the dots change
            m_Root.Add(m_Loading);

            var bottom = new VisualElement();
            bottom.style.position = Position.Absolute;
            bottom.style.left = 0;
            bottom.style.right = 0;
            bottom.style.bottom = 60;
            bottom.style.alignItems = Align.Center;
            bottom.pickingMode = PickingMode.Ignore;
            m_Root.Add(bottom);

            m_Prompt = new Label("Press Any Key to Start");
            m_Prompt.style.fontSize = 18;
            m_Prompt.style.color = Color.white;
            m_Prompt.style.display = DisplayStyle.None;
            bottom.Add(m_Prompt);

            m_Ready = new Label();
            m_Ready.style.fontSize = 14;
            m_Ready.style.color = new Color(0.85f, 0.85f, 0.85f);
            m_Ready.style.marginTop = 6;
            m_Ready.style.display = DisplayStyle.None;
            bottom.Add(m_Ready);

            foreach (Label label in new[] { m_Loading, m_Prompt, m_Ready })
            {
                label.pickingMode = PickingMode.Ignore;
                // Readable over any part of the course.
                label.style.textShadow = new TextShadow { offset = new Vector2(1.5f, 1.5f), blurRadius = 3f, color = new Color(0f, 0f, 0f, 0.85f) };
            }
            GameFont.Apply(m_Root);
        }

        private void UpdateOverlay()
        {
            if (!m_LocallyReady && IsLoadedHere())
            {
                m_LocallyReady = true;
                m_ReadySince = Time.unscaledTime;
            }

            if (m_Root == null) { CheckPress(); return; }

            // Behind the Esc menu if it's opened meanwhile.
            m_Root.style.display = InGameMenu.IsOpen ? DisplayStyle.None : DisplayStyle.Flex;

            if (!m_LocallyReady)
            {
                int dots = 1 + Mathf.FloorToInt((Time.unscaledTime - m_StartTime) / DotSeconds) % 4; // 1, 2, 3, 0
                m_Loading.text = Loc.T("Loading") + new string('.', dots % 4);
                return;
            }

            m_Loading.style.display = DisplayStyle.None;
            CheckPress();

            if (!m_Pressed)
            {
                m_Prompt.text = Loc.T("Press Any Key to Start");
                bool on = Mathf.FloorToInt((Time.unscaledTime - m_ReadySince) / PromptFlashSeconds) % 2 == 0;
                m_Prompt.style.display = DisplayStyle.Flex;
                m_Prompt.style.visibility = on ? Visibility.Visible : Visibility.Hidden;
                m_Ready.style.display = DisplayStyle.None;
            }
            else
            {
                m_Prompt.text = Loc.T("Waiting for other players");
                m_Prompt.style.display = DisplayStyle.Flex;
                m_Prompt.style.visibility = Visibility.Visible;
                m_Ready.text = Loc.F("{0} / {1} ready", m_Round.IntroReadyCount, Mathf.Max(1, m_Round.ActivePlayerCount));
                m_Ready.style.display = DisplayStyle.Flex;
            }
        }

        private void CheckPress()
        {
            if (!m_LocallyReady || m_Pressed || InGameMenu.IsOpen) return;
            if (Time.unscaledTime - m_ReadySince < PressGraceSeconds) return;
            if (!AnyStartPressed()) return;

            m_Pressed = true;
            m_Round.ReportLocalIntroReady();
        }

        /// <summary>Everything this machine needs for the round: the map and challenges, the round itself, our own player.</summary>
        private bool IsLoadedHere()
        {
            if (m_Round == null || !m_Round.IsSpawned) return false;
            if (MatchLayout.Current != null && !MatchLayout.Current.IsBuilt) return false;
            CorePlayerManager local = NetworkPlayers.Local;
            return local != null && local.IsSpawned;
        }

        /// <summary>Any key, mouse button or gamepad button - except the menu ones (Esc / Start).</summary>
        private static bool AnyStartPressed()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.anyKey.wasPressedThisFrame && !keyboard.escapeKey.wasPressedThisFrame) return true;

            Mouse mouse = Mouse.current;
            if (mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)) return true;

            Gamepad pad = Gamepad.current;
            if (pad != null &&
                (pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame ||
                 pad.buttonNorth.wasPressedThisFrame || pad.buttonWest.wasPressedThisFrame ||
                 pad.leftShoulder.wasPressedThisFrame || pad.rightShoulder.wasPressedThisFrame ||
                 pad.selectButton.wasPressedThisFrame))
            {
                return true;
            }
            return false;
        }

        // =====================================================================================
        // Flyover paths
        // =====================================================================================

        /// <summary>
        /// The flyover for <paramref name="layout"/>'s map. Every map uses the placeholder for now; per-map
        /// paths will be chosen here. Null if there's nothing to show yet.
        /// </summary>
        public static IFlyover FlyoverFor(MatchLayout layout)
        {
            switch (MapIdOf(layout))
            {
                case "DinosaurZoo":
                    // From the finish back down the main road to the spawn helipad, turning clockwise so the
                    // dome (north-east of the finish) is the first thing to swing into view.
                    return FinishToSpawn.Create(travelSeconds: 30f, turnDegrees: 180f, clockwise: true) ?? (IFlyover)SpinAtSpawn.Create();
                case "NinjaPirateTower":
                    return NinjaPirateTower(layout.MapInstance.transform);
                case "LastStop":
                    return LastStop(layout.MapInstance.transform);
                default:
                    return SpinAtSpawn.Create();
            }
        }

        /// <summary>The map's catalog id ("DinosaurZoo"), from the instance MatchLayout made ("Map_DinosaurZoo").</summary>
        private static string MapIdOf(MatchLayout layout)
        {
            GameObject map = layout != null ? layout.MapInstance : null;
            if (map == null) return null;
            string name = map.name;
            return name.StartsWith("Map_", System.StringComparison.Ordinal) ? name.Substring(4) : name;
        }

        /// <summary>The same spawn point on every machine: lowest x, then z (not find order).</summary>
        private static bool TryPickSpawn(out Transform spawn)
        {
            spawn = null;
            foreach (PlayerSpawnPoint s in FindObjectsByType<PlayerSpawnPoint>(FindObjectsSortMode.None))
            {
                if (spawn == null) { spawn = s.transform; continue; }
                Vector3 a = s.transform.position, b = spawn.position;
                if (a.x < b.x - 0.01f || (Mathf.Abs(a.x - b.x) <= 0.01f && a.z < b.z)) spawn = s.transform;
            }
            return spawn != null;
        }

        /// <summary>
        /// Ninja Pirate Tower, in the map prefab's own space (the tower's center line is x = -64; its door is in
        /// the +z wall, which is at z = 110..112; the end point sits on the roof's peak at y = 207):
        /// straight down onto the end point at the peak; pull back and tilt up, low over the roof and back along
        /// it past the eave; face the tower and drop straight down its banded wall, past the pirate flag; stop
        /// as the ground (the sand) comes into view and fly in through the door (4 m wide, 6 m tall, at the
        /// bottom of the wall) and the vestibule's inner doorway; then, inside the first floor, tilt up a little,
        /// stop and turn slowly. The camera stays 4 m back from the edge of the big challenge hole in the
        /// middle of the floor. The path was checked against ninja_pirate_tower.te3: it never comes within
        /// 2 m of a tile (the closest is in the 4 m door).
        /// </summary>
        private static IFlyover NinjaPirateTower(Transform map)
        {
            return new KeyframedFlyover(map, spinDegreesPerSecond: 10f, new[]
            {
                //                            time   position (map space)              yaw  pitch (+ = down)
                new KeyframedFlyover.Key(  0.0f, new Vector3(-64f, 226.0f,  64f), 180f,  89.9f), // over the peak
                new KeyframedFlyover.Key(  7.0f, new Vector3(-64f, 204.0f,  86f), 180f,   8.0f), // low over the roof
                new KeyframedFlyover.Key( 11.5f, new Vector3(-64f, 194.0f, 108f), 180f,   6.0f), // nearly at the eave
                new KeyframedFlyover.Key( 16.0f, new Vector3(-64f, 172.0f, 140f), 180f,   0.0f), // facing the wall
                new KeyframedFlyover.Key( 30.0f, new Vector3(-64f,  29.0f, 140f), 180f,   0.0f), // the sand shows
                new KeyframedFlyover.Key( 33.5f, new Vector3(-64f,  23.0f, 122f), 180f,   0.0f),
                new KeyframedFlyover.Key( 36.0f, new Vector3(-64f,  21.5f, 108f), 180f,   0.0f), // through the door
                new KeyframedFlyover.Key( 39.0f, new Vector3(-64f,  21.5f, 100f), 180f, -12.0f), // inside, looking up
            });
        }

        /// <summary>
        /// Last Stop, in the map prefab's own space. The course runs from the spawn pad (centre z ≈ 196.7) to
        /// the finish pad (centre z ≈ -193.3) along x = 453.88, at y = 10 (the top of the walls and pads); both
        /// pads are 16 m square. Straight down onto the finish; pull up and back, tilting up; level off
        /// (30 m above the course, clear of the tallest challenge, ~16 m) and keep backing away down the whole
        /// course, looking at the finish, at up to ~19 m/s; stop just behind the spawn pad and tilt down at it;
        /// move in to stand over the pad, tilting up to look along the course; stop and turn slowly (clockwise).
        /// </summary>
        private static IFlyover LastStop(Transform map)
        {
            const float x = 453.88f, finish = -194.28f, spawn = 195.72f; // the end and spawn points
            return new KeyframedFlyover(map, spinDegreesPerSecond: 10f, new[]
            {
                //                            time   position (map space)                     yaw  pitch (+ = down)
                new KeyframedFlyover.Key(  0.0f, new Vector3(x, 17f, finish),        180f,  89.9f), // over the finish
                new KeyframedFlyover.Key(  4.5f, new Vector3(x, 34f, finish + 22f),  180f,  50.0f), // up and back
                new KeyframedFlyover.Key(  9.0f, new Vector3(x, 40f, finish + 74f),  180f,  10.0f),
                new KeyframedFlyover.Key( 12.0f, new Vector3(x, 40f, finish + 120f), 180f,   3.0f), // levelled off
                new KeyframedFlyover.Key( 26.0f, new Vector3(x, 40f, spawn - 25f),   180f,   3.0f),
                new KeyframedFlyover.Key( 30.0f, new Vector3(x, 40f, spawn + 16f),   180f,   3.0f), // just behind the spawn
                new KeyframedFlyover.Key( 32.5f, new Vector3(x, 40f, spawn + 16f),   180f,  52.0f), // looking down at it
                new KeyframedFlyover.Key( 36.0f, new Vector3(x, 21f, spawn + 8f),    180f,  38.0f), // closer
                new KeyframedFlyover.Key( 39.5f, new Vector3(x, 14f, spawn + 1f),    180f,  -5.0f), // over the pad, looking up
            });
        }

        /// <summary>
        /// A camera path through keyframes given in a map's own space (so it follows the map wherever it's
        /// placed). Position, yaw and pitch are each a monotone cubic through the keys (Fritsch-Carlson): smooth,
        /// never overshooting a key, so a path that hugs a roof or threads a door stays where it was checked. It
        /// starts and ends at rest. After the last key it stays put and turns, easing up to
        /// <c>spinDegreesPerSecond</c> over <see cref="SpinEaseSeconds"/>.
        /// </summary>
        public class KeyframedFlyover : IFlyover
        {
            public const float SpinEaseSeconds = 3f;

            public readonly struct Key
            {
                public readonly float Time;
                public readonly Vector3 Position;
                public readonly float Yaw, Pitch; // degrees; pitch is Unity's (positive looks down)

                public Key(float time, Vector3 position, float yaw, float pitch)
                {
                    Time = time; Position = position; Yaw = yaw; Pitch = pitch;
                }
            }

            private const int Channels = 5; // x, y, z, yaw, pitch

            private readonly Transform m_Space;
            private readonly float[] m_Times;
            private readonly float[][] m_Values;   // [channel][key]
            private readonly float[][] m_Slopes;   // [channel][key], per second
            private readonly float m_Spin;

            public KeyframedFlyover(Transform space, float spinDegreesPerSecond, Key[] keys)
            {
                m_Space = space;
                m_Spin = spinDegreesPerSecond;
                int n = keys.Length;
                m_Times = new float[n];
                m_Values = new float[Channels][];
                m_Slopes = new float[Channels][];
                for (int c = 0; c < Channels; c++) m_Values[c] = new float[n];
                for (int i = 0; i < n; i++)
                {
                    m_Times[i] = keys[i].Time;
                    m_Values[0][i] = keys[i].Position.x;
                    m_Values[1][i] = keys[i].Position.y;
                    m_Values[2][i] = keys[i].Position.z;
                    m_Values[3][i] = keys[i].Yaw;
                    m_Values[4][i] = keys[i].Pitch;
                }
                for (int c = 0; c < Channels; c++) m_Slopes[c] = MonotoneSlopes(m_Times, m_Values[c]);
            }

            public bool TryEvaluate(float t, out Vector3 position, out Quaternion rotation)
            {
                position = default;
                rotation = default;
                if (m_Space == null || m_Times.Length == 0) return false;

                var v = new float[Channels];
                int last = m_Times.Length - 1;
                if (t >= m_Times[last] || last == 0)
                {
                    for (int c = 0; c < Channels; c++) v[c] = m_Values[c][last];
                    float s = Mathf.Max(0f, t - m_Times[last]);
                    // Speed ramps from 0 to m_Spin over the ease, then holds.
                    v[3] += s < SpinEaseSeconds ? m_Spin * s * s / (2f * SpinEaseSeconds) : m_Spin * (s - SpinEaseSeconds * 0.5f);
                }
                else
                {
                    t = Mathf.Max(t, m_Times[0]);
                    int i = 0;
                    while (i < last - 1 && t >= m_Times[i + 1]) i++;
                    float h = m_Times[i + 1] - m_Times[i];
                    float u = h > 0f ? (t - m_Times[i]) / h : 1f;
                    float u2 = u * u, u3 = u2 * u;
                    float h00 = 2f * u3 - 3f * u2 + 1f, h10 = u3 - 2f * u2 + u, h01 = -2f * u3 + 3f * u2, h11 = u3 - u2;
                    for (int c = 0; c < Channels; c++)
                    {
                        float[] p = m_Values[c], m = m_Slopes[c];
                        v[c] = h00 * p[i] + h10 * h * m[i] + h01 * p[i + 1] + h11 * h * m[i + 1];
                    }
                }

                position = m_Space.TransformPoint(new Vector3(v[0], v[1], v[2]));
                rotation = m_Space.rotation * Quaternion.Euler(v[4], v[3], 0f);
                return true;
            }

            /// <summary>Fritsch-Carlson slopes: zero at the ends and at any turning point, limited so no segment overshoots.</summary>
            private static float[] MonotoneSlopes(float[] t, float[] p)
            {
                int n = p.Length;
                var m = new float[n];
                for (int i = 1; i < n - 1; i++)
                {
                    float a = (p[i] - p[i - 1]) / Mathf.Max(1e-4f, t[i] - t[i - 1]);
                    float b = (p[i + 1] - p[i]) / Mathf.Max(1e-4f, t[i + 1] - t[i]);
                    if (a * b <= 0f) continue;
                    float s = 0.5f * (a + b);
                    float limit = 3f * Mathf.Min(Mathf.Abs(a), Mathf.Abs(b));
                    m[i] = Mathf.Abs(s) > limit ? Mathf.Sign(s) * limit : s;
                }
                return m;
            }
        }

        /// <summary>
        /// Starts close in on the finish (a few meters back from it and a little above, looking at it), then
        /// backs away in a straight line to the player spawn while turning at a steady rate, so it has turned
        /// <c>turnDegrees</c> on arrival - then carries on turning there at the same rate. The move eases in
        /// and out over its first and last couple of seconds; the turn never changes speed. The camera's tilt
        /// eases from looking down at the finish to almost level.
        /// </summary>
        public class FinishToSpawn : IFlyover
        {
            public const float StartBack = 9f;        // meters back from the finish, towards the spawn
            public const float StartUp = 3.5f;        // meters above the finish
            public const float EyeHeight = 1.7f;      // above the spawn at the end
            public const float EndPitch = 5f;         // degrees down once there
            public const float EaseSeconds = 2.5f;

            private Vector3 m_Start, m_End;
            private float m_StartYaw, m_StartPitch;
            private float m_Travel, m_DegreesPerSecond;

            public static FinishToSpawn Create(float travelSeconds, float turnDegrees, bool clockwise)
            {
                if (!TryPickSpawn(out Transform spawn)) return null;
                if (!PlayerEndPoint.TryGetNearest(spawn.position, out Vector3 finish)) return null;

                Vector3 end = spawn.position + Vector3.up * EyeHeight;
                Vector3 back = end - finish;
                back.y = 0f;
                if (back.sqrMagnitude < 1f) return null;
                back.Normalize();

                Vector3 start = finish + back * StartBack + Vector3.up * StartUp;
                Vector3 look = finish + Vector3.up * 0.5f - start; // at the finish marker
                float yaw = Quaternion.LookRotation(new Vector3(look.x, 0f, look.z)).eulerAngles.y;
                float pitch = Mathf.Atan2(-look.y, new Vector2(look.x, look.z).magnitude) * Mathf.Rad2Deg;

                travelSeconds = Mathf.Max(1f, travelSeconds);
                return new FinishToSpawn
                {
                    m_Start = start,
                    m_End = end,
                    m_StartYaw = yaw,
                    m_StartPitch = pitch,
                    m_Travel = travelSeconds,
                    m_DegreesPerSecond = (clockwise ? 1f : -1f) * turnDegrees / travelSeconds,
                };
            }

            public bool TryEvaluate(float t, out Vector3 position, out Quaternion rotation)
            {
                float k = Progress(Mathf.Max(0f, t));
                position = Vector3.Lerp(m_Start, m_End, k);
                float pitch = Mathf.Lerp(m_StartPitch, EndPitch, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / m_Travel)));
                rotation = Quaternion.Euler(pitch, m_StartYaw + m_DegreesPerSecond * t, 0f);
                return true;
            }

            /// <summary>0..1 along the path at <paramref name="t"/>: speed ramps up, holds, ramps down (a trapezoid), reaching 1 at the travel time.</summary>
            private float Progress(float t)
            {
                if (t >= m_Travel) return 1f;
                float e = Mathf.Min(EaseSeconds, m_Travel * 0.5f);
                float v = 1f / (m_Travel - e); // top speed, in path fractions per second
                if (t < e) return 0.5f * v * t * t / e;
                if (t > m_Travel - e)
                {
                    float r = m_Travel - t;
                    return 1f - 0.5f * v * r * r / e;
                }
                return 0.5f * v * e + v * (t - e);
            }
        }

        /// <summary>Placeholder: the camera sits at a player spawn point at head height and turns slowly.</summary>
        public class SpinAtSpawn : IFlyover
        {
            public const float EyeHeight = 1.7f;
            public const float DegreesPerSecond = 12f;
            public const float PitchDown = 8f;

            private Vector3 m_Position;
            private float m_StartYaw;

            public static SpinAtSpawn Create()
            {
                if (!TryPickSpawn(out Transform pick)) return null;

                // Start facing the course (towards the nearest end point), like a spawning player.
                Vector3 spawn = pick.position;
                float yaw = pick.eulerAngles.y;
                if (PlayerEndPoint.TryGetNearest(spawn, out Vector3 end))
                {
                    Vector3 to = end - spawn;
                    to.y = 0f;
                    if (to.sqrMagnitude > 0.01f) yaw = Quaternion.LookRotation(to).eulerAngles.y;
                }
                return new SpinAtSpawn { m_Position = spawn + Vector3.up * EyeHeight, m_StartYaw = yaw };
            }

            public bool TryEvaluate(float t, out Vector3 position, out Quaternion rotation)
            {
                position = m_Position;
                rotation = Quaternion.Euler(PitchDown, m_StartYaw + t * DegreesPerSecond, 0f);
                return true;
            }
        }
    }
}
