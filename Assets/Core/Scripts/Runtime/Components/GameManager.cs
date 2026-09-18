using UnityEngine;
using Unity.Netcode;
using System.Collections;
using UnityEngine.UIElements;
using Blocks.Sessions.Common;
using Unity.Services.Multiplayer;
using System.Collections.Generic;
using Cursor = UnityEngine.Cursor;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Manages the game session, UI, and local player lifecycle rules.
    /// Provides a centralized system for handling player spawning, respawning, and session management.
    /// Supports both standard NetworkManager and Unity Multiplayer Services integration.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        #region Fields & Properties

        /// <summary>
        /// Gets the singleton instance of the GameManager.
        /// </summary>
        public static GameManager Instance { get; private set; }

        [Tooltip("If true, uses Unity Multiplayer Services for session management. If false, uses NetworkManager callbacks.")]
        [SerializeField] private bool isUsingMultiplayerServices = false;

        [Header("Session Setup")]
        [Tooltip("Configuration settings for the multiplayer session.")]
        [SerializeField] private SessionSettings sessionSettings;

        [Tooltip("UI Document that displays session/lobby interface.")]
        [SerializeField] private UIDocument sessionUI;

        [Tooltip("Duration in seconds for the session UI fade-out animation.")]
        [SerializeField] private float fadeDuration = 0.5f;

        [Header("Game Rules")]
        [Tooltip("Time in seconds before the local player respawns.")]
        [SerializeField] private float respawnDelay = 5.0f;

        [Tooltip("If true, the local player respawns automatically.")]
        [SerializeField] private bool autoRespawn = true;

        [Header("Spawning")]
        [Tooltip("List of Transforms to use as spawn points. Auto-populated at startup from every " +
            "PlayerSpawnPoint marker found in the scene (see RefreshSpawnPointsFromMarkers) - only used " +
            "as-is, unreplaced, if no PlayerSpawnPoint markers exist yet. Every spawn (initial and " +
            "respawn) picks randomly among these.")]
        [SerializeField] private List<Transform> spawnPoints;

        [Header("Events")]
        [Tooltip("Event raised when a player's stat is depleted (e.g., health reaches zero).")]
        [SerializeField] private StatDepletedEvent onStatDepleted;

        [Tooltip("Event raised to update respawn status UI (countdown timer, messages).")]
        [SerializeField] private RespawnStatusEvent onRespawnStatus;

        [Header("Sound Effects")]
        [Tooltip("Sound effect played during respawn countdown timer ticks.")]
        [SerializeField] private SoundDef respawnTimerSFX;

        private SessionObserver m_SessionObserver;
        private const string k_PlayerNameKey = "_player_name";

        #endregion

        #region Unity Methods

        /// <summary>
        /// Called when the GameManager is destroyed.
        /// Cleans up all event subscriptions and observers to prevent memory leaks.
        /// </summary>
        private void OnDestroy()
        {
            // Cleanup NetworkManager callbacks
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientConnectedCallback -= ClientConnected;
                NetworkManager.Singleton.OnClientDisconnectCallback -= ClientDisconnected;
            }

            // Cleanup SessionObserver
            if (m_SessionObserver != null)
            {
                m_SessionObserver.SessionAdded -= OnSessionAdded;
                m_SessionObserver.Dispose();
                m_SessionObserver = null;
            }
        }

        /// <summary>
        /// Initializes the singleton instance and sets up session observer if using Multiplayer Services.
        /// Prevents duplicate GameManager instances using the singleton pattern with DontDestroyOnLoad.
        /// </summary>
        private void Awake()
        {
            // Enforce singleton pattern
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[GameManager] Duplicate GameManager instance detected. Destroying self.");
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Pick up every PlayerSpawnPoint marker in the scene (see MapObstacleSetup, which generates
            // one per "player_spawn_point" map tile/cluster), so the spawn point set stays in sync with
            // the map without needing to hand-wire the Inspector list every time a map changes.
            RefreshSpawnPointsFromMarkers();

            // Validate required references
            if (sessionUI == null)
            {
                Debug.LogError("[GameManager] SessionUI is not assigned.", this);
            }
            if (sessionSettings == null)
            {
                Debug.LogError("[GameManager] SessionSettings is not assigned.", this);
            }
            if (sessionUI == null || sessionSettings == null)
            {
                return;
            }

            // Initialize Multiplayer Services session observer if enabled
            if (isUsingMultiplayerServices)
            {
                m_SessionObserver = new SessionObserver(sessionSettings.sessionType);
                m_SessionObserver.SessionAdded += OnSessionAdded;
            }
        }

        /// <summary>
        /// Subscribes to NetworkManager callbacks when using standard NetworkManager mode.
        /// Skipped if using Multiplayer Services.
        /// </summary>
        private void Start()
        {
            if (isUsingMultiplayerServices) return;

            if (NetworkManager.Singleton == null)
            {
                Debug.LogError("[GameManager] NetworkManager.Singleton is null. Cannot subscribe to network callbacks.", this);
                return;
            }

            NetworkManager.Singleton.OnClientConnectedCallback += ClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += ClientDisconnected;
        }

        #endregion

        #region NetworkManager Callbacks

        /// <summary>
        /// Handles client disconnection events.
        /// Unregisters from stat depletion events when the local client disconnects.
        /// </summary>
        /// <param name="clientId">The ID of the client that disconnected.</param>
        private void ClientDisconnected(ulong clientId)
        {
            // Only process local client disconnection
            if (clientId != NetworkManager.Singleton.LocalClientId) return;

            if (onStatDepleted != null)
            {
                onStatDepleted.UnregisterListener(HandleStatDepleted);
            }
        }

        /// <summary>
        /// Handles client connection events.
        /// Sets up the local player when they connect, including registering event listeners,
        /// hiding the session UI, setting player name, spawning at initial position, and restoring health.
        /// </summary>
        /// <param name="clientId">The ID of the client that connected.</param>
        private void ClientConnected(ulong clientId)
        {
            // Only process when the local client connects
            if (clientId != NetworkManager.Singleton.LocalClientId) return;

            // Register for stat depletion events (e.g., player death)
            if (onStatDepleted != null)
            {
                onStatDepleted.RegisterListener(HandleStatDepleted);
            }

            // Hide session UI and lock cursor for gameplay
            StartCoroutine(FadeOutAndDisable());
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            StartCoroutine(SetupLocalPlayerWhenReady());
        }

        /// <summary>
        /// A newly-connected client's own LocalClient.PlayerObject isn't always populated the instant
        /// OnClientConnectedCallback fires locally. For the host this reference is normally already set
        /// (its own player object spawns essentially instantly), but a joining client goes through a real
        /// network round-trip first, so the reference can still be null for a frame or more right when its
        /// own "connected" callback fires. The previous code checked once and silently bailed (just a
        /// warning) if it wasn't ready yet - naming, spawn positioning and health restore all got skipped
        /// for that client, most visibly as a joining player showing up nameless ("Player-N" fallback) on
        /// the scoreboard. Retry for a few seconds instead of assuming the first check is enough - mirrors
        /// CoreHUD.RefreshScoreboardUntilComplete's fix for the same class of race.
        /// </summary>
        private IEnumerator SetupLocalPlayerWhenReady()
        {
            const int maxAttempts = 20;
            const float retryDelaySeconds = 0.25f;

            NetworkObject localPlayer = null;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (NetworkManager.Singleton == null || NetworkManager.Singleton.LocalClient == null) yield break;

                localPlayer = NetworkManager.Singleton.LocalClient.PlayerObject;
                if (localPlayer != null) break;

                yield return new WaitForSeconds(retryDelaySeconds);
            }

            if (localPlayer == null)
            {
                Debug.LogWarning("[GameManager] LocalClient.PlayerObject never became available. Cannot setup player.", this);
                yield break;
            }

            // Generate and assign player name based on client ID
            string playerPrefix = "Player";
            string playerNumber = NetworkManager.Singleton.LocalClient.ClientId.ToString();
            string playerName = playerPrefix + playerNumber;

            if (localPlayer.TryGetComponent<CorePlayerState>(out var playerState))
            {
                playerState.SetPlayerName(playerName);
            }
            else
            {
                Debug.LogWarning("[GameManager] CorePlayerState component not found on local player. Cannot set player name.", this);
            }

            // Set spawn position and rotation - a random spawn point, same as respawning.
            if (localPlayer.TryGetComponent<CoreMovement>(out var movement))
            {
                int index = GetRandomSpawnIndex();
                Vector3 spawnPos = GetSpawnPositionForIndex(index);
                if (TryGetSpawnTransform(index, out Transform spawnTransform))
                {
                    movement.transform.rotation = spawnTransform.rotation;
                }

                movement.SetPosition(spawnPos);
                movement.ResetMovementForces();
            }
            else
            {
                Debug.LogWarning("[GameManager] CoreMovement component not found on local player. Cannot set spawn position.", this);
            }

            // Restore full health on spawn
            if (localPlayer.TryGetComponent<CoreStatsHandler>(out var coreStats))
            {
                coreStats.ModifyStat(StatKeys.Health, 100, NetworkManager.Singleton.LocalClientId, ModificationSource.Regeneration);
            }
            else
            {
                Debug.LogWarning("[GameManager] CoreStatsHandler component not found on local player. Cannot restore health.", this);
            }
        }

        #endregion

        #region Multiplayer Services Callbacks

        /// <summary>
        /// Handles session added events from Unity Multiplayer Services.
        /// Performs the same player setup as <see cref="ClientConnected"/> but for Multiplayer Services workflow.
        /// </summary>
        /// <param name="session">The session that was added.</param>
        private void OnSessionAdded(ISession session)
        {
            if (session == null)
            {
                Debug.LogError("[GameManager] OnSessionAdded called with null session.", this);
                return;
            }

            // Subscribe to session removal events
            session.RemovedFromSession += OnRemovedFromSession;

            // Register for stat depletion events
            if (onStatDepleted != null)
            {
                onStatDepleted.RegisterListener(HandleStatDepleted);
            }

            // Hide session UI and lock cursor for gameplay
            StartCoroutine(FadeOutAndDisable());
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            // Set player name from session properties
            SetupLocalPlayerName(session);

            if (NetworkManager.Singleton == null || NetworkManager.Singleton.LocalClient == null)
            {
                Debug.LogWarning("[GameManager] NetworkManager.Singleton or LocalClient is null during session setup.", this);
                return;
            }

            var localPlayer = NetworkManager.Singleton.LocalClient.PlayerObject;
            if (localPlayer == null)
            {
                Debug.LogWarning("[GameManager] LocalClient.PlayerObject is null. Cannot setup player.", this);
                return;
            }

            // Set spawn position and rotation - a random spawn point, same as respawning.
            if (localPlayer.TryGetComponent<CoreMovement>(out var movement))
            {
                int index = GetRandomSpawnIndex();
                Vector3 spawnPos = GetSpawnPositionForIndex(index);
                if (TryGetSpawnTransform(index, out Transform spawnTransform))
                {
                    movement.transform.rotation = spawnTransform.rotation;
                }

                movement.SetPosition(spawnPos);
                movement.ResetMovementForces();
            }
            else
            {
                Debug.LogWarning("[GameManager] CoreMovement component not found on local player. Cannot set spawn position.", this);
            }

            // Restore full health on spawn
            if (localPlayer.TryGetComponent<CoreStatsHandler>(out var coreStats))
            {
                coreStats.ModifyStat(StatKeys.Health, 100, NetworkManager.Singleton.LocalClientId, ModificationSource.Regeneration);
            }
            else
            {
                Debug.LogWarning("[GameManager] CoreStatsHandler component not found on local player. Cannot restore health.", this);
            }
        }

        /// <summary>
        /// Handles removal from a Multiplayer Services session.
        /// Unregisters from stat depletion events when removed from the session.
        /// </summary>
        private void OnRemovedFromSession()
        {
            if (onStatDepleted != null)
            {
                onStatDepleted.UnregisterListener(HandleStatDepleted);
            }
        }

        #endregion

        #region Player Lifecycle

        /// <summary>
        /// Handles death logic for the local player when health is depleted.
        /// Sets player state to Eliminated and initiates respawn routine if auto-respawn is enabled.
        /// Respects <see cref="CorePlayerManager.AutoHandleLifecycle"/> to avoid duplicate lifecycle management.
        /// </summary>
        /// <param name="payload">The stat depletion event payload containing player ID and stat information.</param>
        private void HandleStatDepleted(StatDepletedPayload payload)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.LocalClient == null)
            {
                Debug.LogWarning("[GameManager] Cannot handle stat depletion: NetworkManager.Singleton or LocalClient is null.", this);
                return;
            }

            // Only handle local player's stat depletion
            if (payload.playerId != NetworkManager.Singleton.LocalClientId) return;

            // Only process health depletion (death)
            if (payload.statID == StatKeys.Health)
            {
                var localPlayer = NetworkManager.Singleton.LocalClient.PlayerObject;
                if (localPlayer == null)
                {
                    Debug.LogWarning("[GameManager] Cannot handle player death: LocalClient.PlayerObject is null.", this);
                    return;
                }

                if (localPlayer.TryGetComponent<CorePlayerState>(out var playerState))
                {
                    // Only set state if CorePlayerManager isn't handling lifecycle automatically
                    // This prevents duplicate lifecycle management
                    if (localPlayer.TryGetComponent<CorePlayerManager>(out var playerManager))
                    {
                        if (!playerManager.AutoHandleLifecycle)
                        {
                            playerState.SetLifeState(PlayerLifeState.Eliminated);
                        }
                    }
                    else
                    {
                        // No CorePlayerManager, handle it ourselves
                        playerState.SetLifeState(PlayerLifeState.Eliminated);
                    }

                    // Start respawn countdown if auto-respawn is enabled
                    if (autoRespawn)
                    {
                        StartCoroutine(RespawnRoutine(playerState));
                    }
                }
                else
                {
                    Debug.LogWarning("[GameManager] CorePlayerState component not found on local player. Cannot handle death.", this);
                }
            }
        }

        /// <summary>
        /// Coroutine that handles the respawn countdown and player revival.
        /// Displays countdown UI, then respawns the player at a random spawn point with full health.
        /// </summary>
        /// <param name="playerState">The player state component to respawn.</param>
        /// <returns>Enumerator for coroutine execution.</returns>
        private IEnumerator RespawnRoutine(CorePlayerState playerState)
        {
            if (playerState == null)
            {
                Debug.LogError("[GameManager] RespawnRoutine called with null playerState.", this);
                yield break;
            }

            if (NetworkManager.Singleton == null)
            {
                Debug.LogError("[GameManager] Cannot respawn: NetworkManager.Singleton is null.", this);
                yield break;
            }

            float timer = respawnDelay;
            ulong localId = NetworkManager.Singleton.LocalClientId;

            // Countdown loop: update UI every second
            while (timer > 0)
            {
                if (onRespawnStatus != null)
                {
                    onRespawnStatus.Raise(new RespawnStatusPayload { playerId = localId, message = "RESPAWNING IN", subtext = Mathf.CeilToInt(timer).ToString(), showSubtext = true });
                    PlayRespawnTimerSFX();
                }

                yield return new WaitForSeconds(1.0f);
                timer -= 1.0f;
            }

            // Clear respawn UI
            if (onRespawnStatus != null)
            {
                onRespawnStatus.Raise(new RespawnStatusPayload { playerId = localId, message = "", subtext = "", showSubtext = false });
            }

            // Respawn at a random spawn point, same as the initial spawn.
            var coreMovement = playerState.GetComponent<CoreMovement>();
            if (coreMovement != null)
            {
                int spawnIndex = GetRandomSpawnIndex();
                if (TryGetSpawnTransform(spawnIndex, out Transform spawnTransform))
                {
                    coreMovement.transform.rotation = spawnTransform.rotation;
                    coreMovement.SetPosition(spawnTransform.position);
                }
                else
                {
                    coreMovement.SetPosition(Vector3.zero);
                }

                coreMovement.ResetMovementForces();
                PlayRespawnTimerSFX();
            }
            else
            {
                Debug.LogWarning("[GameManager] CoreMovement component not found during respawn. Cannot reposition player.", this);
            }

            // Restore full health
            var coreStats = playerState.GetComponent<CoreStatsHandler>();
            if (coreStats != null)
            {
                coreStats.ModifyStat(StatKeys.Health, 100, localId, ModificationSource.Regeneration);
            }
            else
            {
                Debug.LogWarning("[GameManager] CoreStatsHandler component not found during respawn. Cannot restore health.", this);
            }

            // Update player state to respawned
            playerState.SetLifeState(PlayerLifeState.Respawned);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Plays the respawn timer sound effect locally (not networked).
        /// Uses spatial blend of 0 in the SoundDef, so position has no effect on panning or attenuation.
        /// </summary>
        private void PlayRespawnTimerSFX()
        {
            if (respawnTimerSFX != null)
            {
                CoreDirector.RequestAudio(respawnTimerSFX)
                     .WithPosition(Vector3.zero)
                     .Play();
            }
        }

        /// <summary>
        /// Finds every <see cref="PlayerSpawnPoint"/> marker currently in the scene and uses them as the
        /// spawn point set, overwriting whatever was hand-wired in the Inspector - keeps spawnPoints in
        /// sync with the map automatically, since PlayerSpawnPoint markers are generated straight from
        /// each map's "player_spawn_point" tiles (see MapObstacleSetup.BuildSpawnPoint). Leaves the
        /// Inspector-assigned list untouched if no markers are found, so a scene that hasn't been
        /// migrated to markers yet keeps working exactly as before.
        ///
        /// Called from GetRandomSpawnIndex (i.e. right before every spawn/respawn), not just once from
        /// Awake - GameManager is a DontDestroyOnLoad singleton that can Awake in a boot/lobby scene
        /// well before the actual map (and its PlayerSpawnPoint markers) has loaded, so an Awake-only
        /// refresh would find zero markers and silently keep whatever stale list was serialized on the
        /// GameManager instance (including entries pointing at spawn objects that have since been
        /// deleted from the map). Refreshing at spawn time instead means this always reflects whatever
        /// map is actually loaded when a player needs a spawn position.
        /// </summary>
        private void RefreshSpawnPointsFromMarkers()
        {
            var markers = FindObjectsByType<PlayerSpawnPoint>(FindObjectsSortMode.None);
            if (markers == null || markers.Length == 0)
            {
                return;
            }

            spawnPoints = new List<Transform>(markers.Length);
            foreach (PlayerSpawnPoint marker in markers)
            {
                if (marker != null)
                {
                    spawnPoints.Add(marker.transform);
                }
            }
        }

        /// <summary>
        /// Returns a random spawn index. Used for every spawn - initial and respawn alike - so players
        /// don't all funnel through the same handful of spots and spawn camping is harder. Refreshes the
        /// spawn point set from any PlayerSpawnPoint markers in the scene first (see
        /// RefreshSpawnPointsFromMarkers) so this always reflects whatever map is currently loaded.
        /// </summary>
        /// <returns>A random spawn point index, or -1 if no spawn points are configured.</returns>
        private int GetRandomSpawnIndex()
        {
            RefreshSpawnPointsFromMarkers();
            if (spawnPoints == null || spawnPoints.Count == 0) return -1;
            return Random.Range(0, spawnPoints.Count);
        }

        /// <summary>
        /// Safely resolves the spawn point Transform at the given index, without throwing on an invalid
        /// index or on a list entry that was never assigned (or was assigned to something since
        /// destroyed) - either of those used to surface as an UnassignedReferenceException/
        /// NullReferenceException that aborted spawn positioning partway through, leaving the player
        /// wherever they happened to already be instead of at a fallback position.
        /// </summary>
        /// <param name="index">The spawn point index to resolve.</param>
        /// <param name="spawnTransform">The resolved Transform, or null if unavailable.</param>
        /// <returns>True if a usable spawn point Transform was found.</returns>
        private bool TryGetSpawnTransform(int index, out Transform spawnTransform)
        {
            spawnTransform = null;
            if (index < 0 || spawnPoints == null || spawnPoints.Count <= index)
            {
                return false;
            }

            spawnTransform = spawnPoints[index];
            return spawnTransform != null;
        }

        /// <summary>
        /// Returns the world position of the spawn point at the given index. Defaults to Vector3.zero if
        /// the index is invalid, no spawn points are configured, or the entry at that index is unusable
        /// (see TryGetSpawnTransform). Deliberately just the marker's raw position with no
        /// ground-snapping - PlayerSpawnPoint markers sit a little above the true floor by design, so a
        /// spawned player drops the remaining distance naturally under gravity.
        /// </summary>
        /// <param name="index">The spawn point index to get the position for.</param>
        /// <returns>The world position of the spawn point at the given index.</returns>
        private Vector3 GetSpawnPositionForIndex(int index)
        {
            if (!TryGetSpawnTransform(index, out Transform spawnTransform))
            {
                Debug.LogWarning($"[GameManager] No usable spawn point at index {index}. Using Vector3.zero as spawn position.", this);
                return Vector3.zero;
            }

            return spawnTransform.position;
        }

        /// <summary>
        /// Sets up the local player's name from the Multiplayer Services session properties.
        /// Retrieves the player name from session data and assigns it to <see cref="CorePlayerState"/>.
        /// </summary>
        /// <param name="session">The active multiplayer session.</param>
        private void SetupLocalPlayerName(ISession session)
        {
            if (session == null || session.CurrentPlayer == null)
            {
                Debug.LogWarning("[GameManager] Cannot setup player name: session or CurrentPlayer is null.", this);
                return;
            }

            var player = session.CurrentPlayer;
            string playerName = "Player";
            if (player.Properties.TryGetValue(k_PlayerNameKey, out var nameProp))
            {
                playerName = nameProp.Value;
            }

            if (NetworkManager.Singleton == null || NetworkManager.Singleton.LocalClient == null)
            {
                Debug.LogWarning("[GameManager] Cannot setup player name: NetworkManager.Singleton or LocalClient is null.", this);
                return;
            }

            if (NetworkManager.Singleton.LocalClient.PlayerObject == null)
            {
                Debug.LogWarning("[GameManager] Cannot setup player name: LocalClient.PlayerObject is null.", this);
                return;
            }

            var playerState = NetworkManager.Singleton.LocalClient.PlayerObject.GetComponent<CorePlayerState>();
            if (playerState != null)
            {
                playerState.SetPlayerName(playerName);
            }
            else
            {
                Debug.LogWarning("[GameManager] CorePlayerState component not found on local player. Cannot set player name.", this);
            }
        }

        /// <summary>
        /// Coroutine that smoothly fades out and hides the session UI.
        /// Interpolates opacity from current value to 0 over the fade duration, then hides the UI completely.
        /// </summary>
        /// <returns>Enumerator for coroutine execution.</returns>
        private IEnumerator FadeOutAndDisable()
        {
            if (sessionUI == null) yield break;

            VisualElement root = sessionUI.rootVisualElement;
            float startOpacity = root.resolvedStyle.opacity;

            // Handle edge case where opacity is already near zero
            if (startOpacity < 0.01f) startOpacity = 1f;

            float elapsedTime = 0f;
            while (elapsedTime < fadeDuration)
            {
                elapsedTime += Time.deltaTime;
                float t = Mathf.Clamp01(elapsedTime / fadeDuration);
                root.style.opacity = Mathf.Lerp(startOpacity, 0f, t);
                yield return null;
            }

            // Ensure fully faded and hidden
            root.style.opacity = 0f;
            root.style.display = DisplayStyle.None;
        }

        #endregion
    }
}
