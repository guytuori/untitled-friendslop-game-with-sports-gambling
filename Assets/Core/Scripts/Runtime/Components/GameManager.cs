using UnityEngine;
using Fusion;
using System.Collections;
using UnityEngine.UIElements;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using Cursor = UnityEngine.Cursor;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Manages the gameplay scene's local player lifecycle: spawning this client's own player avatar
    /// (Photon Fusion, Shared mode - see FusionSessionService), picking spawn points, and the death /
    /// respawn rules. One per gameplay scene (it lives on the "[BB] GameManager" prefab placed in the scene).
    ///
    /// Spawning: once the scene is part of a running Fusion session, this spawns the local player's avatar
    /// (<see cref="playerPrefab"/>) at a random <see cref="PlayerSpawnPoint"/> with this client as its State
    /// Authority. Every client does this for itself, so every player ends up with exactly one avatar that
    /// they own - the Fusion replacement for NGO's automatic player-prefab spawning.
    ///
    /// Playing the scene directly in the editor (no session from the menus): starts this machine's dev
    /// session first (FusionSessionService.StartDevSessionAsync), so testing still needs no menus -
    /// Multiplayer Play Mode virtual players playing the same scene join the same session.
    ///
    /// If the session ends underneath the player (connection lost), returns to <see cref="exitSceneName"/>.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        #region Fields & Properties

        /// <summary>
        /// Gets the instance of the GameManager for the current gameplay scene.
        /// </summary>
        public static GameManager Instance { get; private set; }

        [Header("Players")]
        [Tooltip("The player avatar prefab (must have a Fusion NetworkObject). Each client spawns its own.")]
        [SerializeField] private GameObject playerPrefab;

        [Tooltip("Scene to return to if the session ends (connection lost) while in this scene.")]
        [SerializeField] private string exitSceneName = "MainMenu";

        [Header("Session UI")]
        [Tooltip("Optional UI Document shown until the local player has spawned (e.g. a 'connecting' overlay). Faded out on spawn.")]
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

        private bool m_HasSpawnedLocalPlayer;
        private bool m_ListeningForDeath;
        private bool m_Exiting;

        #endregion

        #region Unity Methods

        private void Awake()
        {
            // One per gameplay scene. (It used to be a DontDestroyOnLoad singleton, which made a second
            // match after returning to the menus keep the first match's stale instance.)
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[GameManager] Duplicate GameManager in the scene. Destroying this one.", this);
                Destroy(gameObject);
                return;
            }

            Instance = this;

            // Pick up every PlayerSpawnPoint marker in the scene (see MapObstacleSetup, which generates
            // one per "player_spawn_point" map tile/cluster), so the spawn point set stays in sync with
            // the map without needing to hand-wire the Inspector list every time a map changes.
            RefreshSpawnPointsFromMarkers();

            if (playerPrefab == null)
            {
                Debug.LogError("[GameManager] Player Prefab is not assigned - no player will spawn.", this);
            }
        }

        private void Start()
        {
            NetworkPlayers.LocalPlayerSpawned += HandleLocalPlayerSpawned;
            FusionSessionService.Instance.GameEnded += HandleGameEnded;
            StartCoroutine(SpawnLocalPlayerWhenReady());
        }

        private void OnDestroy()
        {
            NetworkPlayers.LocalPlayerSpawned -= HandleLocalPlayerSpawned;
            if (FusionSessionService.HasInstance)
            {
                FusionSessionService.Instance.GameEnded -= HandleGameEnded;
            }

            if (m_ListeningForDeath && onStatDepleted != null)
            {
                onStatDepleted.UnregisterListener(HandleStatDepleted);
            }

            if (Instance == this) Instance = null;
        }

        #endregion

        #region Session & Spawning

        /// <summary>
        /// Waits for this scene to be part of a running session (starting a dev session first if the
        /// scene was played directly), then spawns this client's own player avatar.
        /// </summary>
        private IEnumerator SpawnLocalPlayerWhenReady()
        {
            FusionSessionService service = FusionSessionService.Instance;

            if (service.GameRunner == null && !service.IsStarting)
            {
                // Played directly (not reached through Start Match) - see the class summary.
                var devStart = service.StartDevSessionAsync(gameObject.scene);
                while (!devStart.IsCompleted) yield return null;
                if (devStart.Result != null)
                {
                    Debug.LogError($"[GameManager] Couldn't start a dev session: {devStart.Result}", this);
                    yield break;
                }
            }

            // Wait until the runner is up and has finished loading (and registering) this scene.
            NetworkRunner runner = null;
            while (true)
            {
                if (this == null) yield break;
                runner = service.GameRunner;
                if (runner != null && runner.IsRunning && !runner.IsSceneManagerBusy && runner.CanSpawn) break;
                if (runner == null && !service.IsStarting) yield break; // session ended while waiting
                yield return null;
            }

            if (m_HasSpawnedLocalPlayer || playerPrefab == null) yield break;
            m_HasSpawnedLocalPlayer = true;

            int index = GetRandomSpawnIndex();
            Vector3 position = GetSpawnPositionForIndex(index);
            Quaternion rotation = TryGetSpawnTransform(index, out Transform spawnTransform) ? spawnTransform.rotation : Quaternion.identity;

            NetworkObject avatar = runner.Spawn(playerPrefab, position, rotation, runner.LocalPlayer,
                flags: NetworkSpawnFlags.SharedModeStateAuthLocalPlayer);
            if (avatar != null)
            {
                runner.SetPlayerObject(runner.LocalPlayer, avatar);
            }
            else
            {
                Debug.LogError("[GameManager] Spawning the player avatar failed - does the player prefab have a Fusion NetworkObject?", this);
            }
        }

        /// <summary>
        /// Runs once this client's own avatar has spawned (see NetworkPlayers): names it, gives it full
        /// health, starts listening for its death, and switches the cursor to gameplay mode.
        /// </summary>
        private void HandleLocalPlayerSpawned(CorePlayerManager localPlayer)
        {
            if (localPlayer == null) return;
            StartCoroutine(SetUpLocalPlayer(localPlayer));
        }

        private IEnumerator SetUpLocalPlayer(CorePlayerManager localPlayer)
        {
            // Fusion calls Spawned() on the avatar's components one after another - give the rest of
            // them (CorePlayerState, CoreStatsHandler, ...) a moment to finish spawning first.
            for (int frame = 0; frame < 30; frame++)
            {
                if (localPlayer == null) yield break;
                bool stateReady = localPlayer.PlayerState == null || localPlayer.PlayerState.IsSpawned;
                bool statsReady = localPlayer.CoreStats == null || localPlayer.CoreStats.IsSpawned;
                if (stateReady && statsReady) break;
                yield return null;
            }
            if (localPlayer == null) yield break;

            if (!m_ListeningForDeath && onStatDepleted != null)
            {
                onStatDepleted.RegisterListener(HandleStatDepleted);
                m_ListeningForDeath = true;
            }

            StartCoroutine(FadeOutAndDisable());
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            if (localPlayer.PlayerState != null)
            {
                localPlayer.PlayerState.SetPlayerName(PlayerProfile.DisplayName);
            }

            if (localPlayer.CoreMovement != null)
            {
                localPlayer.CoreMovement.ResetMovementForces();
            }

            if (localPlayer.CoreStats != null)
            {
                localPlayer.CoreStats.ModifyStat(StatKeys.Health, 100, localPlayer.OwnerClientId, ModificationSource.Regeneration);
            }
        }

        private void HandleGameEnded(string message)
        {
            if (m_Exiting) return;
            m_Exiting = true;

            Debug.Log($"[GameManager] {message} Returning to {exitSceneName}.");
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            SceneManager.LoadScene(exitSceneName);
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
            CorePlayerManager localPlayer = NetworkPlayers.Local;
            if (localPlayer == null) return;

            // Only handle local player's stat depletion
            if (payload.playerId != localPlayer.OwnerClientId) return;

            // Only process health depletion (death)
            if (payload.statID != StatKeys.Health) return;

            CorePlayerState playerState = localPlayer.PlayerState;
            if (playerState == null)
            {
                Debug.LogWarning("[GameManager] CorePlayerState component not found on local player. Cannot handle death.", this);
                return;
            }

            // Only set state if CorePlayerManager isn't handling lifecycle automatically
            // This prevents duplicate lifecycle management
            if (!localPlayer.AutoHandleLifecycle)
            {
                playerState.SetLifeState(PlayerLifeState.Eliminated);
            }

            // Start respawn countdown if auto-respawn is enabled
            if (autoRespawn)
            {
                StartCoroutine(RespawnRoutine(playerState));
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

            float timer = respawnDelay;
            ulong localId = playerState.OwnerClientId;

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

            // The avatar may have despawned during the countdown (left the session).
            if (playerState == null || !playerState.IsSpawned) yield break;

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
        /// migrated to markers yet keeps working exactly as before. Called right before every
        /// spawn/respawn (from GetRandomSpawnIndex) as well as from Awake, so it always reflects the map
        /// that's actually loaded.
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
        /// destroyed).
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
