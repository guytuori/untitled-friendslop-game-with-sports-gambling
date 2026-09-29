using System;
using System.Collections.Generic;
using System.IO;
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
    /// Everything Photon Fusion 2 for the menus: hosting a game, joining one, the public game list the
    /// Game Browser shows, and the pre-match lobby roster. A single DontDestroyOnLoad object created on
    /// first use (<see cref="Instance"/>) - nothing needs to be placed in a scene.
    ///
    /// Two separate NetworkRunners, since a runner can only be used for one thing:
    ///   - The lobby runner joins Fusion's ClientServer session lobby purely to receive the list of open,
    ///     visible games (OnSessionListUpdated). Used by the Game Browser; shut down when it closes.
    ///   - The game runner is the actual hosted/joined session (GameMode.Host / GameMode.Client).
    ///
    /// ROOM CODES: the session's name is its room code - 5 characters from an alphabet without look-alike
    /// characters (no 0/O, 1/I/L). Uniqueness is checked by Photon itself: creating a Host session whose
    /// name already exists fails with ShutdownReason.GameIdAlreadyExists, in which case a new code is
    /// generated and it tries again. Joining by code (private games) is just joining a session by name,
    /// and public games are joined the same way from the browser.
    ///
    /// Public/Private is the session's IsVisible flag: private games never appear in the lobby list but
    /// can still be joined by code. The host's rules are published as session properties (see
    /// <see cref="PropertyKeys"/>) so the browser can show and filter them.
    ///
    /// ROSTER: the host sees every player through OnPlayerJoined/OnPlayerLeft and, on every change, sends
    /// the roster to each client as a small reliable data message (<see cref="RosterKey"/>) - so clients
    /// see the same player list without any networked objects (which would need Fusion's code weaving and
    /// prefab setup this menu flow doesn't otherwise need).
    ///
    /// START MATCH (host only): closes and hides the session so nobody else can find or join it, then loads
    /// <see cref="GameplaySceneName"/> through Fusion's scene manager, which brings every client along.
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

        /// <summary>
        /// The gameplay scene Start Match loads for everyone (must be in the build settings). Thrash is
        /// currently the only gameplay scene there.
        /// </summary>
        public const string GameplaySceneName = "Thrash";

        private const string RoomCodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // 31 chars, no 0/O/1/I/L
        private const int MaxHostAttempts = 5;

        private static readonly ReliableKey RosterKey = ReliableKey.FromInts(0x5050, 1, 0, 0); // "PP" roster, v1

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
        private readonly List<LobbyPlayer> m_Players = new List<LobbyPlayer>();

        public bool InGame => m_GameRunner != null && m_GameRunner.IsRunning && !m_Starting;
        public bool IsHost => InGame && m_GameRunner.IsServer;
        public string RoomCode { get; private set; } = "";
        public bool IsPublic { get; private set; }
        public int MaxPlayers { get; private set; }
        public IReadOnlyList<LobbyPlayer> Players => m_Players;

        /// <summary>Raised whenever the lobby roster changes (join/leave, or a roster message from the host).</summary>
        public event Action RosterChanged;

        /// <summary>Raised when the game session ends without this player choosing to leave (host left, connection lost), with a player-facing message.</summary>
        public event Action<string> GameEnded;

        private void OnDestroy()
        {
            if (s_Instance == this) s_Instance = null;
        }

        // =====================================================================================
        // Lobby
        // =====================================================================================

        /// <summary>Joins the session lobby if not already in it (or joining). Safe to call repeatedly. Not while in a game.</summary>
        public async void EnsureInLobby()
        {
            if (m_LobbyRunner != null || m_GameRunner != null) return;

            HasSessionList = false;
            NetworkRunner runner = CreateRunner("[FusionLobbyRunner]", out _);
            m_LobbyRunner = runner;

            StartGameResult result;
            try
            {
                result = await runner.JoinSessionLobby(SessionLobby.ClientServer, null, null, CreateAppSettings());
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
        /// Creates a new Host session with <paramref name="rules"/>. Returns null on success (the room code
        /// is then <see cref="RoomCode"/>), or a player-facing error message.
        /// </summary>
        public async Task<string> HostGameAsync(HostGameRulesData rules)
        {
            if (m_GameRunner != null) return "Already in a game.";
            await ShutdownLobbyAsync();

            for (int attempt = 0; attempt < MaxHostAttempts; attempt++)
            {
                string code = NewRoomCode();
                var args = new StartGameArgs
                {
                    GameMode = GameMode.Host,
                    SessionName = code,
                    PlayerCount = rules.MaxPlayers,
                    IsVisible = rules.IsPublic,
                    IsOpen = true,
                    SessionProperties = BuildSessionProperties(rules),
                    CustomPhotonAppSettings = CreateAppSettings(),
                };

                StartOutcome result = await StartGameRunner(args);
                if (result.Ok)
                {
                    RoomCode = code;
                    IsPublic = rules.IsPublic;
                    MaxPlayers = rules.MaxPlayers;
                    RebuildHostRoster();
                    return null;
                }

                if (result.Reason != ShutdownReason.GameIdAlreadyExists)
                {
                    return DescribeFailure(result.Reason);
                }
                // Code collision - try another one.
            }

            return "Couldn't create a unique room code. Try again.";
        }

        /// <summary>Joins the session with room code <paramref name="code"/> (case-insensitive). Returns null on success, or a player-facing error message.</summary>
        public async Task<string> JoinGameAsync(string code)
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
                GameMode = GameMode.Client,
                SessionName = code,
                CustomPhotonAppSettings = CreateAppSettings(),
            };

            StartOutcome result = await StartGameRunner(args);
            if (!result.Ok)
            {
                if (wasInLobby) EnsureInLobby();
                return DescribeFailure(result.Reason);
            }

            RoomCode = code;
            SessionInfo info = m_GameRunner.SessionInfo;
            IsPublic = info != null && info.IsVisible;
            MaxPlayers = info != null ? info.MaxPlayers : 0;
            // The host sends the roster as soon as it sees this player join; until then show just us.
            m_Players.Clear();
            m_Players.Add(new LobbyPlayer { PlayerId = m_GameRunner.LocalPlayer.PlayerId, IsLocal = true });
            RosterChanged?.Invoke();
            return null;
        }

        /// <summary>Leaves the current game (host: closes it for everyone).</summary>
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

        /// <summary>Host only: closes and hides the session, then loads the gameplay scene for every player.</summary>
        public bool StartMatch()
        {
            if (!IsHost) return false;

            SessionInfo info = m_GameRunner.SessionInfo;
            info.IsOpen = false;
            info.IsVisible = false;

            var sceneManager = m_GameRunner.GetComponent<NetworkSceneManagerDefault>();
            SceneRef scene = sceneManager != null ? sceneManager.GetSceneRef(GameplaySceneName) : SceneRef.None;
            if (!scene.IsValid)
            {
                Debug.LogError($"[Fusion] '{GameplaySceneName}' isn't in the build settings - can't start the match.");
                return false;
            }

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
            m_GameRunner = CreateRunner("[FusionGameRunner]", out NetworkSceneManagerDefault sceneManager);
            args.SceneManager = sceneManager;

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
            m_Players.Clear();
        }

        // =====================================================================================
        // Helpers
        // =====================================================================================

        private NetworkRunner CreateRunner(string name, out NetworkSceneManagerDefault sceneManager)
        {
            var go = new GameObject(name);
            DontDestroyOnLoad(go);
            var runner = go.AddComponent<NetworkRunner>();
            runner.ProvideInput = false;
            runner.AddCallbacks(this);
            sceneManager = go.AddComponent<NetworkSceneManagerDefault>();
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

        private static Dictionary<string, SessionProperty> BuildSessionProperties(HostGameRulesData rules)
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
                [PropertyKeys.Items] = rules.ItemsEnabled,
                [PropertyKeys.Pickups] = rules.PickupsEnabled,
            };
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

        private void RebuildHostRoster()
        {
            if (m_GameRunner == null || !m_GameRunner.IsServer) return;

            var ids = new List<int>();
            foreach (PlayerRef player in m_GameRunner.ActivePlayers)
            {
                ids.Add(player.PlayerId);
            }
            ids.Sort();

            int hostId = m_GameRunner.LocalPlayer.PlayerId;
            ApplyRoster(hostId, MaxPlayers, ids);

            // Send the roster to every client.
            byte[] message = EncodeRoster(hostId, MaxPlayers, ids);
            foreach (PlayerRef player in m_GameRunner.ActivePlayers)
            {
                if (player == m_GameRunner.LocalPlayer) continue;
                m_GameRunner.SendReliableDataToPlayer(player, RosterKey, message);
            }
        }

        private void ApplyRoster(int hostId, int maxPlayers, List<int> ids)
        {
            int localId = m_GameRunner != null ? m_GameRunner.LocalPlayer.PlayerId : -1;
            MaxPlayers = maxPlayers;
            m_Players.Clear();
            foreach (int id in ids)
            {
                m_Players.Add(new LobbyPlayer { PlayerId = id, IsHost = id == hostId, IsLocal = id == localId });
            }
            RosterChanged?.Invoke();
        }

        private static byte[] EncodeRoster(int hostId, int maxPlayers, List<int> ids)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(hostId);
            writer.Write(maxPlayers);
            writer.Write(ids.Count);
            foreach (int id in ids) writer.Write(id);
            writer.Flush();
            return stream.ToArray();
        }

        private void DecodeAndApplyRoster(ReadOnlySpan<byte> data)
        {
            try
            {
                using var stream = new MemoryStream(data.ToArray());
                using var reader = new BinaryReader(stream);
                int hostId = reader.ReadInt32();
                int maxPlayers = reader.ReadInt32();
                int count = reader.ReadInt32();
                var ids = new List<int>(count);
                for (int i = 0; i < count; i++) ids.Add(reader.ReadInt32());
                ApplyRoster(hostId, maxPlayers, ids);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Fusion] Bad roster message: {e.Message}");
            }
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
                m_Sessions.Add(new GameSessionListing
                {
                    SessionName = info.Name,
                    PlayerCount = info.PlayerCount,
                    Rules = ReadRules(info)
                });
            }

            HasSessionList = true;
            SessionListUpdated?.Invoke(m_Sessions);
        }

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            if (runner == m_GameRunner && runner.IsServer && !m_Starting) RebuildHostRoster();
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            if (runner == m_GameRunner && runner.IsServer) RebuildHostRoster();
        }

        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data)
        {
            if (runner != m_GameRunner || runner.IsServer) return;
            if (key == RosterKey) DecodeAndApplyRoster(data);
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
                    ? "The host closed the game."
                    : $"Disconnected from the game ({shutdownReason}).");
            }
        }

        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        {
            // Followed by OnShutdown, which handles cleanup.
        }

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
