using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Fusion;
using Fusion.Photon.Realtime;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blocks.Gameplay.Core
{
    /// <summary>One player in the pre-match lobby (see FusionSessionService.Players).</summary>
    public struct LobbyPlayer
    {
        public int PlayerId;
        public bool IsHost;
        public bool IsLocal;
    }

    /// <summary>
    /// Everything Photon Fusion 2 for the game: hosting a game, joining one, the public game list the
    /// Game Browser shows, the pre-match lobby roster, starting the match, and the running session the
    /// gameplay scene uses (<see cref="GameRunner"/>). A single DontDestroyOnLoad object created on first
    /// use (<see cref="Instance"/>) - nothing needs to be placed in a scene.
    ///
    /// SHARED MODE: every session runs in Fusion's Shared mode. Each player's own client has State
    /// Authority over their own player object (that's how the gameplay code was written - it was built on
    /// Netcode for GameObjects' Distributed Authority mode, which works the same way), and the session's
    /// master client owns the scene objects (RoundTimer, ChallengeManager, ...). "Host" in the menus is
    /// simply the master client: whoever created the game, or - if they leave - whoever Photon promotes
    /// next, so a game never dies just because its host quit.
    ///
    /// Two separate NetworkRunners, since a runner can only be used for one thing:
    ///   - The lobby runner joins Fusion's Shared session lobby purely to receive the list of open,
    ///     visible games (OnSessionListUpdated). Used by the Game Browser; shut down when it closes.
    ///   - The game runner is the actual session (<see cref="GameRunner"/>), kept through the lobby and
    ///     into the match.
    ///
    /// ROOM CODES: the session's name is its room code - 5 characters from an alphabet without look-alike
    /// characters (no 0/O, 1/I/L). In Shared mode, starting a session by name joins it if it exists and
    /// creates it otherwise, so both directions are checked afterwards with the host's random
    /// "hn" (host nonce) session property: a host that finds someone else's nonce hit an existing code and
    /// retries with a new one, and a joiner that finds no nonce at all created an empty session by mistake
    /// (a typo'd code) and leaves it again - that accidental session is created closed and hidden, so
    /// nobody else can ever end up in it.
    ///
    /// Public/Private is the session's IsVisible flag: private games never appear in the lobby list but
    /// can still be joined by code. The host's rules are published as session properties so the browser
    /// can show and filter them, and so every player in the match can read them (<see cref="SessionRules"/>).
    ///
    /// ROSTER: in Shared mode every client sees every player (Runner.ActivePlayers, OnPlayerJoined/Left),
    /// so each client builds the lobby roster itself.
    ///
    /// START MATCH (host only): closes and hides the session so nobody else can find or join it, then loads
    /// <see cref="GameplaySceneName"/> through Fusion's scene manager, which brings every client along.
    /// The gameplay scene's GameManager then spawns each player's own avatar.
    ///
    /// PLAYING A GAMEPLAY SCENE DIRECTLY (editor testing): GameManager calls <see cref="StartDevSessionAsync"/>,
    /// which starts a private Shared session named after this machine and the scene, so every instance
    /// on this machine (e.g. Multiplayer Play Mode virtual players) that plays the same scene ends up in
    /// the same session - no menus needed.
    ///
    /// REGION: fixed to <see cref="Region"/> for now. Photon sessions only exist within one region, and
    /// "best region" can pick different regions for players who are in the same country, which would
    /// make their games invisible to each other.
    /// </summary>
    public sealed class FusionSessionService : MonoBehaviour, INetworkRunnerCallbacks
    {
        /// <summary>Photon region every player connects to - see the class summary. "us" = US East.</summary>
        public const string Region = "us";

        public const int RoomCodeLength = 5;

        /// <summary>Start Match needs at least this many players in the session, host included.</summary>
        public const int MinPlayersToStart = 2;

        /// <summary>
        /// The gameplay scene Start Match loads for everyone (must be in the build settings). Thrash is
        /// currently the only gameplay scene there.
        /// </summary>
        public const string GameplaySceneName = "Thrash";

        private const string RoomCodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // 31 chars, no 0/O/1/I/L
        private const int MaxHostAttempts = 5;
        private const int DevSessionMaxPlayers = 8;

        /// <summary>Session property keys (kept short - they're sent to every player browsing the lobby).</summary>
        private static class PropertyKeys
        {
            public const string Map = "map";
            public const string RoundTime = "rt";
            public const string BonusTime = "bt";
            public const string Lives = "lv";
            public const string StartingPoints = "sp";
            public const string DeathPenalty = "dp";
            public const string WagerPayoutTenths = "wp"; // session properties can't be floats - 2.5 is stored as 25
            public const string Items = "it";
            public const string Pickups = "pu";
            public const string CreatedUnixSeconds = "ct"; // when the host created it - for oldest-first ordering
            public const string HostNonce = "hn";         // random number from the creating host - see the class summary
        }

        private static FusionSessionService s_Instance;

        public static FusionSessionService Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    var go = new GameObject("[FusionSessionService]");
                    DontDestroyOnLoad(go);
                    s_Instance = go.AddComponent<FusionSessionService>();
                }
                return s_Instance;
            }
        }

        /// <summary>Whether the service exists yet - lets code check for a running session without creating it.</summary>
        public static bool HasInstance => s_Instance != null;

        // ----- lobby (game list) -----

        private NetworkRunner m_LobbyRunner;
        private readonly List<GameSessionListing> m_Sessions = new List<GameSessionListing>();

        /// <summary>Latest list of open public games. Raised every time Photon sends an update.</summary>
        public event Action<IReadOnlyList<GameSessionListing>> SessionListUpdated;

        /// <summary>Raised if joining the lobby fails, with a player-facing message.</summary>
        public event Action<string> LobbyError;

        public IReadOnlyList<GameSessionListing> Sessions => m_Sessions;
        public bool HasSessionList { get; private set; }

        // ----- game (hosted/joined session) -----

        private NetworkRunner m_GameRunner;
        private bool m_Starting;
        private bool m_Leaving;
        private int m_RosterMasterId = -1;
        private readonly List<LobbyPlayer> m_Players = new List<LobbyPlayer>();

        /// <summary>The running game session's runner (null when not in a game, and while one is still starting).</summary>
        public NetworkRunner GameRunner => m_GameRunner != null && !m_Starting ? m_GameRunner : null;

        /// <summary>True while a session is being started (host, join, quick match or dev session).</summary>
        public bool IsStarting => m_Starting;

        public bool InGame => m_GameRunner != null && m_GameRunner.IsRunning && !m_Starting;

        /// <summary>This player is the session's master client - the host, as far as the menus are concerned.</summary>
        public bool IsHost => InGame && m_GameRunner.IsSharedModeMasterClient;

        public string RoomCode { get; private set; } = "";
        public bool IsPublic { get; private set; }
        public int MaxPlayers { get; private set; }
        public IReadOnlyList<LobbyPlayer> Players => m_Players;

        /// <summary>True while in a session started by <see cref="StartDevSessionAsync"/> (a gameplay scene played directly).</summary>
        public bool IsDevSession { get; private set; }

        /// <summary>The rules the host published for the current session (Normal defaults if none).</summary>
        public HostGameRulesData SessionRules
        {
            get
            {
                SessionInfo info = InGame ? m_GameRunner.SessionInfo : null;
                return info != null && info.IsValid ? ReadRules(info) : new HostGameRulesData();
            }
        }

        /// <summary>Raised whenever the lobby roster changes (join/leave, or a new host).</summary>
        public event Action RosterChanged;

        /// <summary>Raised when the game session ends without this player choosing to leave (connection lost, kicked), with a player-facing message.</summary>
        public event Action<string> GameEnded;

        private void OnDestroy()
        {
            if (s_Instance == this) s_Instance = null;
        }

        private void Update()
        {
            // Host migration: when the master client leaves, Photon promotes someone else, which isn't
            // necessarily visible yet in the OnPlayerLeft callback - rebuild the roster once it is.
            if (!InGame) return;
            int masterId = m_GameRunner.GetMasterClient().PlayerId;
            if (masterId != m_RosterMasterId) RebuildRoster();
        }

        // =====================================================================================
        // Lobby
        // =====================================================================================

        /// <summary>Joins the session lobby if not already in it (or joining). Safe to call repeatedly. Not while in a game.</summary>
        public async void EnsureInLobby()
        {
            if (m_LobbyRunner != null || m_GameRunner != null) return;

            HasSessionList = false;
            NetworkRunner runner = CreateRunner("[FusionLobbyRunner]", out _, out _);
            m_LobbyRunner = runner;

            StartGameResult result;
            try
            {
                result = await runner.JoinSessionLobby(SessionLobby.Shared, null, null, CreateAppSettings());
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                result = null;
            }

            if (m_LobbyRunner != runner)
            {
                // Left the lobby (or started a game) while the join was still in flight.
                if (runner != null && runner.IsRunning) _ = runner.Shutdown();
                return;
            }

            if (result == null || !result.Ok)
            {
                string reason = result != null ? result.ShutdownReason.ToString() : "exception";
                Debug.LogWarning($"[Fusion] Couldn't join the session lobby: {reason} {result?.ErrorMessage}");
                await ShutdownLobbyAsync();
                LobbyError?.Invoke(result != null ? DescribeFailure(result.ShutdownReason) : "Couldn't connect to the game server.");
            }
        }

        /// <summary>Leaves the session lobby (e.g. when the Game Browser closes). Doesn't affect a hosted/joined game.</summary>
        public void LeaveLobby() => _ = ShutdownLobbyAsync();

        /// <summary>Leaves the lobby and waits for its runner to finish shutting down, so a game runner never starts while it's still running.</summary>
        private async Task ShutdownLobbyAsync()
        {
            NetworkRunner runner = m_LobbyRunner;
            m_LobbyRunner = null;
            HasSessionList = false;
            m_Sessions.Clear();

            if (runner == null) return;
            try
            {
                if (runner.IsRunning) await runner.Shutdown();
                else Destroy(runner.gameObject);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        // =====================================================================================
        // Hosting / joining
        // =====================================================================================

        /// <summary>
        /// Creates a new session with <paramref name="rules"/>. Returns null on success (the room code
        /// is then <see cref="RoomCode"/>), or a player-facing error message.
        /// </summary>
        public async Task<string> HostGameAsync(HostGameRulesData rules)
        {
            if (m_GameRunner != null) return "Already in a game.";
            await ShutdownLobbyAsync();

            for (int attempt = 0; attempt < MaxHostAttempts; attempt++)
            {
                string code = NewRoomCode();
                int nonce = NewNonce();
                var args = new StartGameArgs
                {
                    GameMode = GameMode.Shared,
                    SessionName = code,
                    PlayerCount = rules.MaxPlayers,
                    IsVisible = rules.IsPublic,
                    IsOpen = true,
                    SessionProperties = BuildSessionProperties(rules, nonce),
                    CustomPhotonAppSettings = CreateAppSettings(),
                };

                StartOutcome result = await StartGameRunner(args);
                if (!result.Ok)
                {
                    return DescribeFailure(result.Reason);
                }

                if (ReadNonce(m_GameRunner.SessionInfo) == nonce)
                {
                    RoomCode = code;
                    IsPublic = rules.IsPublic;
                    MaxPlayers = rules.MaxPlayers;
                    OnSessionReady();
                    return null;
                }

                // Code collision - we joined someone else's session. Leave it and try another code.
                Debug.Log($"[Fusion] Room code {code} was already taken - trying another one.");
                await LeaveGameAsync();
            }

            return "Couldn't create a unique room code. Try again.";
        }

        /// <summary>Joins the session with room code <paramref name="code"/> (case-insensitive). Returns null on success, or a player-facing error message.</summary>
        public Task<string> JoinGameAsync(string code) => JoinGameAsync(code, rejoinLobbyOnFailure: true);

        private async Task<string> JoinGameAsync(string code, bool rejoinLobbyOnFailure)
        {
            if (m_GameRunner != null) return "Already in a game.";

            code = NormalizeCode(code);
            if (code.Length == 0) return "Enter a room code first.";

            // Only one runner at a time - leave the lobby first, and rejoin it if the join fails (so the
            // Game Browser keeps its list).
            bool wasInLobby = m_LobbyRunner != null;
            await ShutdownLobbyAsync();

            var args = new StartGameArgs
            {
                GameMode = GameMode.Shared,
                SessionName = code,
                // Only used if no session with this code exists and Shared mode creates one - see the
                // class summary. Closed and hidden, so nobody else can join it before we leave it again.
                IsOpen = false,
                IsVisible = false,
                CustomPhotonAppSettings = CreateAppSettings(),
            };

            StartOutcome result = await StartGameRunner(args);
            if (!result.Ok)
            {
                if (wasInLobby && rejoinLobbyOnFailure) EnsureInLobby();
                return DescribeFailure(result.Reason);
            }

            SessionInfo info = m_GameRunner.SessionInfo;
            if (ReadNonce(info) == 0)
            {
                // No real game has this code - Shared mode just created an empty one for us.
                await LeaveGameAsync();
                if (wasInLobby && rejoinLobbyOnFailure) EnsureInLobby();
                return DescribeFailure(ShutdownReason.GameNotFound);
            }

            RoomCode = code;
            IsPublic = info != null && info.IsVisible;
            MaxPlayers = info != null ? info.MaxPlayers : 0;
            OnSessionReady();
            return null;
        }

        /// <summary>
        /// Quick match for the Join Game > Public Normal / Hard / Very Hard buttons: joins the oldest open
        /// public game that matches <paramref name="filter"/> and isn't full, or - if there isn't one (or
        /// every attempt fails, e.g. it filled up or started in the meantime) - hosts a new public game with
        /// <paramref name="hostRules"/>. Returns null on success (joined or hosting; see <see cref="IsHost"/>),
        /// or a player-facing error message.
        /// </summary>
        public async Task<string> QuickMatchAsync(GameSearchFilter filter, HostGameRulesData hostRules)
        {
            if (m_GameRunner != null) return "Already in a game.";

            IReadOnlyList<GameSessionListing> sessions = await GetSessionListAsync();
            if (sessions == null) return "Couldn't connect to the game server.";

            var candidates = new List<GameSessionListing>();
            foreach (GameSessionListing session in sessions) // already sorted oldest first
            {
                if (session.Rules == null || !filter.Matches(session.Rules)) continue;
                if (session.Rules.MaxPlayers > 0 && session.PlayerCount >= session.Rules.MaxPlayers) continue;
                candidates.Add(session);
            }

            const int MaxJoinAttempts = 3;
            for (int i = 0; i < candidates.Count && i < MaxJoinAttempts; i++)
            {
                string error = await JoinGameAsync(candidates[i].SessionName, rejoinLobbyOnFailure: false);
                if (error == null) return null;
                Debug.Log($"[Fusion] Quick match couldn't join {candidates[i].SessionName} ({error}) - trying the next one.");
            }

            HostGameRulesData rules = hostRules.Clone();
            rules.IsPublic = true;
            return await HostGameAsync(rules);
        }

        /// <summary>
        /// Joins the lobby if needed and waits for the first session list. Returns null if the lobby
        /// couldn't be joined. If the lobby is joined but Photon never sends a list (it may not when there
        /// are no games at all), returns an empty list after a few seconds.
        /// </summary>
        private async Task<IReadOnlyList<GameSessionListing>> GetSessionListAsync()
        {
            if (HasSessionList) return new List<GameSessionListing>(m_Sessions);

            var gotList = new TaskCompletionSource<bool>();
            void OnList(IReadOnlyList<GameSessionListing> _) => gotList.TrySetResult(true);
            void OnError(string _) => gotList.TrySetResult(false);
            SessionListUpdated += OnList;
            LobbyError += OnError;

            try
            {
                EnsureInLobby();
                Task finished = await Task.WhenAny(gotList.Task, Task.Delay(6000));
                if (finished == gotList.Task)
                {
                    return gotList.Task.Result ? new List<GameSessionListing>(m_Sessions) : null;
                }

                // Timed out: fine if we're connected to the lobby (just no games), otherwise a failure.
                return m_LobbyRunner != null && m_LobbyRunner.IsRunning ? new List<GameSessionListing>() : null;
            }
            finally
            {
                SessionListUpdated -= OnList;
                LobbyError -= OnError;
            }
        }

        /// <summary>
        /// Gameplay scene played directly (editor testing - see the class summary): starts or joins this
        /// machine's private dev session for <paramref name="scene"/>, taking over the already-loaded scene
        /// so its scene objects are networked. Returns null on success, or an error message.
        /// </summary>
        public async Task<string> StartDevSessionAsync(Scene scene)
        {
            if (m_GameRunner != null) return m_Starting ? "Already starting." : null;
            await ShutdownLobbyAsync();

            var sceneInfo = new NetworkSceneInfo();
            if (scene.buildIndex >= 0 && scene.buildIndex < SceneManager.sceneCountInBuildSettings)
            {
                sceneInfo.AddSceneRef(SceneRef.FromIndex(scene.buildIndex), LoadSceneMode.Additive);
            }
            else
            {
                Debug.LogWarning($"[Fusion] '{scene.name}' isn't in the Build Settings, so its scene objects (RoundTimer, " +
                    "ChallengeManager, ...) won't be networked when it's played directly. Players still spawn and move.");
            }

            string machine = ((uint)SystemInfo.deviceUniqueIdentifier.GetHashCode()).ToString("X8");
            string sessionName = $"DEV-{machine}-{scene.name}";
            var args = new StartGameArgs
            {
                GameMode = GameMode.Shared,
                SessionName = sessionName,
                PlayerCount = DevSessionMaxPlayers,
                IsVisible = false,
                IsOpen = true,
                Scene = sceneInfo,
                CustomPhotonAppSettings = CreateAppSettings(),
            };

            StartOutcome result = await StartGameRunner(args);
            if (!result.Ok) return DescribeFailure(result.Reason);

            IsDevSession = true;
            RoomCode = sessionName;
            IsPublic = false;
            MaxPlayers = DevSessionMaxPlayers;
            OnSessionReady();
            return null;
        }

        /// <summary>Leaves the current game. If this player was the host, another player becomes the host.</summary>
        public async Task LeaveGameAsync()
        {
            if (m_GameRunner == null) return;

            m_Leaving = true;
            NetworkRunner runner = m_GameRunner;
            m_GameRunner = null;
            ResetGameState();
            try
            {
                await runner.Shutdown();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            m_Leaving = false;
        }

        /// <summary>Host only, and only with at least <see cref="MinPlayersToStart"/> players: closes and hides the session, then loads the gameplay scene for every player.</summary>
        public bool StartMatch()
        {
            if (!IsHost) return false;
            if (m_Players.Count < MinPlayersToStart)
            {
                Debug.LogWarning($"[Fusion] Start Match needs at least {MinPlayersToStart} players.");
                return false;
            }

            var sceneManager = m_GameRunner.GetComponent<NetworkSceneManagerDefault>();
            SceneRef scene = sceneManager != null ? sceneManager.GetSceneRef(GameplaySceneName) : SceneRef.None;
            if (!scene.IsValid)
            {
                Debug.LogError($"[Fusion] '{GameplaySceneName}' isn't in the build settings - can't start the match.");
                return false;
            }

            SessionInfo info = m_GameRunner.SessionInfo;
            info.IsOpen = false;
            info.IsVisible = false;

            m_GameRunner.LoadScene(scene, LoadSceneMode.Single, LocalPhysicsMode.None, true);
            return true;
        }

        /// <summary>
        /// Outcome of StartGameRunner. Our own small struct rather than Fusion's StartGameResult, since
        /// StartGameResult can't be constructed outside Fusion (needed for the exception case).
        /// </summary>
        private readonly struct StartOutcome
        {
            public readonly bool Ok;
            public readonly ShutdownReason Reason;

            public StartOutcome(bool ok, ShutdownReason reason)
            {
                Ok = ok;
                Reason = reason;
            }
        }

        private async Task<StartOutcome> StartGameRunner(StartGameArgs args)
        {
            m_Starting = true;
            m_GameRunner = CreateRunner("[FusionGameRunner]", out NetworkSceneManagerDefault sceneManager, out NetworkObjectProviderDefault objectProvider);
            args.SceneManager = sceneManager;
            args.ObjectProvider = objectProvider;

            // Keep simulating (and stay connected) while the window isn't focused - needed for testing
            // several instances on one machine. NGO's NetworkManager used to set this ("Run In Background").
            Application.runInBackground = true;

            StartOutcome outcome;
            try
            {
                StartGameResult result = await m_GameRunner.StartGame(args);
                outcome = new StartOutcome(result.Ok, result.ShutdownReason);
                if (!result.Ok)
                {
                    Debug.LogWarning($"[Fusion] StartGame ({args.GameMode}, '{args.SessionName}') failed: {result.ShutdownReason} {result.ErrorMessage}");
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                outcome = new StartOutcome(false, ShutdownReason.Error);
            }
            m_Starting = false;

            if (!outcome.Ok)
            {
                ShutdownRunner(ref m_GameRunner);
            }

            return outcome;
        }

        private void ResetGameState()
        {
            RoomCode = "";
            IsPublic = false;
            MaxPlayers = 0;
            IsDevSession = false;
            m_RosterMasterId = -1;
            m_Players.Clear();
            SessionProfiles.Clear();
        }

        /// <summary>A session has started and been checked: build the roster and tell everyone who we are (SessionProfiles).</summary>
        private void OnSessionReady()
        {
            RebuildRoster();
            SessionProfiles.BroadcastLocal(m_GameRunner);
        }

        // =====================================================================================
        // Helpers
        // =====================================================================================

        private NetworkRunner CreateRunner(string name, out NetworkSceneManagerDefault sceneManager, out NetworkObjectProviderDefault objectProvider)
        {
            var go = new GameObject(name);
            DontDestroyOnLoad(go);
            var runner = go.AddComponent<NetworkRunner>();
            runner.ProvideInput = false;
            runner.AddCallbacks(this);
            sceneManager = go.AddComponent<NetworkSceneManagerDefault>();
            objectProvider = go.AddComponent<NetworkObjectProviderDefault>();
            return runner;
        }

        private static void ShutdownRunner(ref NetworkRunner runner)
        {
            if (runner == null) return;
            NetworkRunner r = runner;
            runner = null;
            if (r.IsRunning) _ = r.Shutdown();
            else if (r != null) Destroy(r.gameObject);
        }

        private static FusionAppSettings CreateAppSettings()
        {
            FusionAppSettings settings = PhotonAppSettings.Global.AppSettings.GetCopy();
            settings.FixedRegion = Region;
            return settings;
        }

        private static string NewRoomCode()
        {
            byte[] bytes = Guid.NewGuid().ToByteArray();
            var chars = new char[RoomCodeLength];
            for (int i = 0; i < RoomCodeLength; i++)
            {
                chars[i] = RoomCodeAlphabet[bytes[i] % RoomCodeAlphabet.Length];
            }
            return new string(chars);
        }

        /// <summary>A random non-zero number (0 means "no nonce" - see ReadNonce).</summary>
        private static int NewNonce()
        {
            int nonce = BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0) & int.MaxValue;
            return nonce == 0 ? 1 : nonce;
        }

        public static string NormalizeCode(string code) => (code ?? "").Trim().ToUpperInvariant();

        private static string DescribeFailure(ShutdownReason reason)
        {
            switch (reason)
            {
                case ShutdownReason.GameNotFound: return "No game with that code.";
                case ShutdownReason.GameIsFull: return "That game is full.";
                case ShutdownReason.GameClosed: return "That game has already started.";
                case ShutdownReason.MaxCcuReached: return "The game server is at capacity. Try again later.";
                case ShutdownReason.InvalidAuthentication:
                case ShutdownReason.InvalidRegion:
                    return "Couldn't connect to Photon (check the App ID in PhotonAppSettings).";
                case ShutdownReason.PhotonCloudTimeout:
                case ShutdownReason.ConnectionTimeout:
                case ShutdownReason.OperationTimeout:
                case ShutdownReason.ConnectionRefused:
                    return "Couldn't reach the game server.";
                default: return $"Couldn't connect ({reason}).";
            }
        }

        private static Dictionary<string, SessionProperty> BuildSessionProperties(HostGameRulesData rules, int nonce)
        {
            return new Dictionary<string, SessionProperty>
            {
                [PropertyKeys.Map] = rules.MapName ?? "",
                [PropertyKeys.RoundTime] = rules.RoundTimeSeconds,
                [PropertyKeys.BonusTime] = rules.BonusTimeSeconds,
                [PropertyKeys.Lives] = rules.Lives,
                [PropertyKeys.StartingPoints] = rules.StartingPoints,
                [PropertyKeys.DeathPenalty] = rules.DeathPenalty,
                [PropertyKeys.WagerPayoutTenths] = Mathf.RoundToInt(rules.WagerPayout * 10f),
                [PropertyKeys.CreatedUnixSeconds] = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                [PropertyKeys.Items] = rules.ItemsEnabled,
                [PropertyKeys.Pickups] = rules.PickupsEnabled,
                [PropertyKeys.HostNonce] = nonce,
            };
        }

        private static int ReadCreatedUnixSeconds(SessionInfo info)
        {
            if (info.Properties != null
                && info.Properties.TryGetValue(PropertyKeys.CreatedUnixSeconds, out SessionProperty created)
                && created.IsInt)
            {
                return (int)created;
            }
            return 0;
        }

        /// <summary>The creating host's nonce, or 0 if the session has none (it wasn't created by HostGameAsync).</summary>
        private static int ReadNonce(SessionInfo info)
        {
            if (info != null && info.IsValid && info.Properties != null
                && info.Properties.TryGetValue(PropertyKeys.HostNonce, out SessionProperty nonce)
                && nonce.IsInt)
            {
                return (int)nonce;
            }
            return 0;
        }

        /// <summary>Reads a session's published rules back into a HostGameRulesData (missing properties keep the Normal defaults).</summary>
        private static HostGameRulesData ReadRules(SessionInfo info)
        {
            var rules = new HostGameRulesData { IsPublic = info.IsVisible, MaxPlayers = info.MaxPlayers };
            var props = info.Properties;
            if (props == null) return rules;

            if (props.TryGetValue(PropertyKeys.Map, out SessionProperty p) && p.IsString) rules.MapName = (string)p;
            if (props.TryGetValue(PropertyKeys.RoundTime, out p) && p.IsInt) rules.RoundTimeSeconds = (int)p;
            if (props.TryGetValue(PropertyKeys.BonusTime, out p) && p.IsInt) rules.BonusTimeSeconds = (int)p;
            if (props.TryGetValue(PropertyKeys.Lives, out p) && p.IsInt) rules.Lives = (int)p;
            if (props.TryGetValue(PropertyKeys.StartingPoints, out p) && p.IsInt) rules.StartingPoints = (int)p;
            if (props.TryGetValue(PropertyKeys.DeathPenalty, out p) && p.IsInt) rules.DeathPenalty = (int)p;
            if (props.TryGetValue(PropertyKeys.WagerPayoutTenths, out p) && p.IsInt) rules.WagerPayout = (int)p / 10f;
            if (props.TryGetValue(PropertyKeys.Items, out p) && p.Isbool) rules.ItemsEnabled = (bool)p;
            if (props.TryGetValue(PropertyKeys.Pickups, out p) && p.Isbool) rules.PickupsEnabled = (bool)p;
            return rules;
        }

        // =====================================================================================
        // Roster
        // =====================================================================================

        /// <summary>Rebuilds the lobby roster from the session's players (every client sees everyone in Shared mode).</summary>
        private void RebuildRoster()
        {
            if (m_GameRunner == null || !m_GameRunner.IsRunning) return;

            var ids = new List<int>();
            foreach (PlayerRef player in m_GameRunner.ActivePlayers)
            {
                ids.Add(player.PlayerId);
            }
            ids.Sort();

            int localId = m_GameRunner.LocalPlayer.PlayerId;
            int masterId = m_GameRunner.GetMasterClient().PlayerId;
            m_RosterMasterId = masterId;

            m_Players.Clear();
            foreach (int id in ids)
            {
                m_Players.Add(new LobbyPlayer { PlayerId = id, IsHost = id == masterId, IsLocal = id == localId });
            }
            RosterChanged?.Invoke();
        }

        // =====================================================================================
        // INetworkRunnerCallbacks
        // =====================================================================================

        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
        {
            if (runner != m_LobbyRunner) return;

            m_Sessions.Clear();
            foreach (SessionInfo info in sessionList)
            {
                if (info == null || !info.IsValid || !info.IsOpen || !info.IsVisible) continue;
                if (ReadNonce(info) == 0) continue; // not a game created through Host Game
                m_Sessions.Add(new GameSessionListing
                {
                    SessionName = info.Name,
                    PlayerCount = info.PlayerCount,
                    Rules = ReadRules(info),
                    CreatedUnixSeconds = ReadCreatedUnixSeconds(info)
                });
            }

            // Photon doesn't guarantee any order - oldest first, so the longest-waiting games are at the top.
            m_Sessions.Sort((a, b) => a.CreatedUnixSeconds.CompareTo(b.CreatedUnixSeconds));

            HasSessionList = true;
            SessionListUpdated?.Invoke(m_Sessions);
        }

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            if (runner != m_GameRunner || m_Starting) return;
            RebuildRoster();
            // Everyone re-sends their profile so the newcomer learns every name and character.
            SessionProfiles.BroadcastLocal(runner);
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            if (runner != m_GameRunner || m_Starting) return;
            SessionProfiles.Remove(player.PlayerId);
            RebuildRoster();
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            if (runner == m_LobbyRunner)
            {
                m_LobbyRunner = null;
                HasSessionList = false;
                return;
            }

            if (runner != m_GameRunner || m_Starting) return;

            m_GameRunner = null;
            ResetGameState();
            if (!m_Leaving)
            {
                GameEnded?.Invoke(shutdownReason == ShutdownReason.Ok || shutdownReason == ShutdownReason.DisconnectedByPluginLogic
                    ? "The game was closed."
                    : $"Disconnected from the game ({shutdownReason}).");
            }
        }

        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        {
            // Followed by OnShutdown, which handles cleanup.
        }

        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }
        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnInput(NetworkRunner runner, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
    }
}
