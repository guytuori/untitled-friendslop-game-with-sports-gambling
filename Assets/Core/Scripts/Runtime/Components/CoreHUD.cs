using UnityEngine;
using Unity.Netcode;
using System.Collections;
using UnityEngine.UIElements;
using System.Collections.Generic;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Manages the player's heads-up display (HUD) using Unity's UI Toolkit.
    /// This component handles health/stamina bars, notifications, respawn overlays, and player status updates.
    /// Only active for the local player (owner).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class CoreHUD : NetworkBehaviour
    {
        #region Fields & Properties

        [Header("Component Dependencies")]
        [SerializeField] private CoreStatsHandler coreStats;

        [Header("Listening to Events")]
        [SerializeField] private StatChangeEvent onStatChanged;
        [SerializeField] private NotificationEvent onNotification;
        [SerializeField] private RespawnStatusEvent onRespawnStatusChanged;

        // UI Element References
        private Label m_MessageLabel;
        private Label m_CountdownLabel;
        private UIDocument m_UIDocument;
        private ProgressBar m_PlayerHealthBar;
        private ProgressBar m_PlayerStaminaBar;
        private VisualElement m_MessageOverlay;
        private VisualElement m_PlayerHealthBarFill;
        private VisualElement m_PlayerStaminaBarFill;
        private VisualElement m_NotificationContainer;
        private VisualElement m_ScoreboardContainer;
        private Label m_RoundTimerLabel;
        private VisualElement m_ChallengeOverlay;
        private Label m_ChallengeTitleLabel;
        private Label m_ChallengeSubtitleLabel;
        private ProgressBar m_ChallengeTimerBar;
        private VisualElement m_ChallengeBetButtons;
        private Button m_ChallengeBetSuccessButton;
        private Button m_ChallengeBetFailureButton;
        private Button m_ChallengeBetDeclineButton;
        private Label m_ChallengeBetStatusLabel;
        private VisualElement m_ChallengeBetAnnouncements;
        private bool m_HasPlacedBetThisWindow;

        // Notification System
        private readonly List<NotificationData> m_ActiveNotifications = new List<NotificationData>();

        // Scoreboard System
        private readonly Dictionary<ulong, ScoreboardRow> m_ScoreboardRows = new Dictionary<ulong, ScoreboardRow>();

        // Lifecycle Management
        private Coroutine m_EliminatedCoroutine;
        private Coroutine m_ScoreboardRefreshCoroutine;

        // Constants
        private const int k_MaxNotifications = 3;
        private const float k_NotificationDuration = 3f;
        private static readonly Color k_StaminaBarColor = new Color(0.83f, 0.29f, 0.29f, 0.75f);
        private static readonly Color k_HealthBarColor = new Color(0.29f, 0.83f, 0.43f, 0.75f);

        #endregion

        #region Nested Classes

        /// <summary>
        /// Represents a single notification with its UI element and lifecycle management.
        /// </summary>
        private class NotificationData
        {
            public VisualElement Element;
            public Label Label;
            public Coroutine LifetimeCoroutine;
            public string Message;
        }

        /// <summary>
        /// One displayed row of the scoreboard: its UI element, the score label within it, and the specific
        /// player's PlayerScore it's bound to (so its NetworkVariable subscription can be cleanly removed later).
        /// </summary>
        private class ScoreboardRow
        {
            public VisualElement Element;
            public Label ScoreLabel;
            public PlayerScore PlayerScoreComponent;

            public void HandleScoreChanged(int previousValue, int newValue)
            {
                if (ScoreLabel != null)
                {
                    ScoreLabel.text = newValue.ToString();
                }
            }
        }

        #endregion

        #region Unity & Network Lifecycle

        /// <summary>
        /// Handles network spawn initialization for the local player's HUD.
        /// </summary>
        public override void OnNetworkSpawn()
        {
            if (!IsOwner)
            {
                DisableForNonOwner();
                return;
            }

            if (!ValidateComponents()) return;

            CreateHUD();
            Initialize();
            RegisterEventListeners();
            StartCoroutine(InitialHUDUpdate());

            NetworkManager.Singleton.OnClientConnectedCallback += HandleScoreboardRosterChanged;
            NetworkManager.Singleton.OnClientDisconnectCallback += HandleScoreboardRosterChanged;
            RequestScoreboardRefresh();

            if (RoundTimer.Instance != null)
            {
                RoundTimer.Instance.TimeRemaining.OnValueChanged += HandleRoundTimerChanged;
                UpdateTimerLabel(RoundTimer.Instance.TimeRemaining.Value);
            }

            if (ChallengeManager.Instance != null)
            {
                ChallengeManager.Instance.IsBettingWindowActive.OnValueChanged += HandleBettingWindowActiveChanged;
                ChallengeManager.Instance.BettingTimeRemaining.OnValueChanged += HandleBettingTimeRemainingChanged;
                ChallengeManager.Instance.ActiveBettorClientIds.OnListChanged += HandleActiveBettorsChanged;
                RefreshChallengeOverlay();
            }
        }

        /// <summary>
        /// Called during OnNetworkSpawn to allow derived classes to perform additional initialization.
        /// </summary>
        protected virtual void Initialize()
        {
            // Override in derived classes for additional initialization
        }

        /// <summary>
        /// Handles cleanup when the network object is despawned.
        /// </summary>
        public override void OnNetworkDespawn()
        {
            if (IsOwner)
            {
                UnregisterEventListeners();
                ClearAllNotifications();

                if (NetworkManager.Singleton != null)
                {
                    NetworkManager.Singleton.OnClientConnectedCallback -= HandleScoreboardRosterChanged;
                    NetworkManager.Singleton.OnClientDisconnectCallback -= HandleScoreboardRosterChanged;
                }

                if (RoundTimer.Instance != null)
                {
                    RoundTimer.Instance.TimeRemaining.OnValueChanged -= HandleRoundTimerChanged;
                }

                if (ChallengeManager.Instance != null)
                {
                    ChallengeManager.Instance.IsBettingWindowActive.OnValueChanged -= HandleBettingWindowActiveChanged;
                    ChallengeManager.Instance.BettingTimeRemaining.OnValueChanged -= HandleBettingTimeRemainingChanged;
                    ChallengeManager.Instance.ActiveBettorClientIds.OnListChanged -= HandleActiveBettorsChanged;
                }

                ClearScoreboardRows();
            }
        }

        #endregion

        #region Event Management

        /// <summary>
        /// Registers all event listeners for HUD updates.
        /// </summary>
        private void RegisterEventListeners()
        {
            if (onStatChanged != null) onStatChanged.RegisterListener(HandleStatChanged);
            if (onRespawnStatusChanged != null) onRespawnStatusChanged.RegisterListener(HandleRespawnStatusChanged);
            if (onNotification != null) onNotification.RegisterListener(HandleNotification);

            RegisterAdditionalListeners();
        }

        /// <summary>
        /// Registers additional event listeners.
        /// </summary>
        protected virtual void RegisterAdditionalListeners()
        {
        }

        /// <summary>
        /// Unregisters all event listeners to prevent memory leaks.
        /// </summary>
        private void UnregisterEventListeners()
        {
            if (onStatChanged != null) onStatChanged.UnregisterListener(HandleStatChanged);
            if (onRespawnStatusChanged != null) onRespawnStatusChanged.UnregisterListener(HandleRespawnStatusChanged);
            if (onNotification != null) onNotification.UnregisterListener(HandleNotification);

            UnregisterAdditionalListeners();
        }

        /// <summary>
        /// Unregisters additional event listeners.
        /// </summary>
        protected virtual void UnregisterAdditionalListeners()
        {
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Handles respawn status changes, updating the overlay message and countdown.
        /// </summary>
        /// <param name="payload">The respawn status payload.</param>
        private void HandleRespawnStatusChanged(RespawnStatusPayload payload)
        {
            if (payload.playerId != OwnerClientId) return;

            var divider = m_MessageOverlay?.Q<VisualElement>("message-divider");

            // Hide overlay if both message and subtext are empty
            if (string.IsNullOrEmpty(payload.message) && string.IsNullOrEmpty(payload.subtext))
            {
                if (m_MessageOverlay != null)
                {
                    m_MessageOverlay.style.display = DisplayStyle.None;
                }
                return;
            }

            // Show overlay with message
            if (m_MessageOverlay != null)
            {
                m_MessageOverlay.style.display = DisplayStyle.Flex;

                if (m_MessageLabel != null)
                {
                    m_MessageLabel.text = payload.message;
                }

                // Control countdown/divider visibility
                if (m_CountdownLabel != null)
                {
                    m_CountdownLabel.text = payload.subtext;
                    m_CountdownLabel.style.display = payload.showSubtext ? DisplayStyle.Flex : DisplayStyle.None;
                }

                if (divider != null)
                {
                    divider.style.display = payload.showSubtext ? DisplayStyle.Flex : DisplayStyle.None;
                }
            }
        }

        /// <summary>
        /// Handles stat change events, updating progress bars and handling elimination notifications.
        /// </summary>
        /// <param name="payload">The stat change payload.</param>
        private void HandleStatChanged(StatChangePayload payload)
        {
            // Handle elimination notifications
            ProcessEliminationNotification(payload);

            // Process networked stat changes (available to all clients)
            HandleStatChangedNetworked(payload);

            // Process local player stat changes
            if (payload.targetPlayerId == OwnerClientId)
            {
                UpdateLocalPlayerStats(payload);
                HandleStatChangedLocal(payload);
            }
        }

        /// <summary>
        /// Handles local player stat changes.
        /// </summary>
        /// <param name="payload">The stat change payload.</param>
        protected virtual void HandleStatChangedLocal(StatChangePayload payload)
        {
            // Override in derived classes to handle additional local stat changes
        }

        /// <summary>
        /// Handles networked stat changes visible to all clients.
        /// </summary>
        /// <param name="payload">The stat change payload.</param>
        protected virtual void HandleStatChangedNetworked(StatChangePayload payload)
        {
            // Override in derived classes to handle additional networked stat changes
        }

        /// <summary>
        /// Handles notification events by displaying them in the HUD.
        /// </summary>
        /// <param name="payload">The notification payload containing the client ID and message.</param>
        private void HandleNotification(NotificationPayload payload)
        {
            AddNotification(payload.message);
        }

        #endregion

        #region UI Creation & Setup

        /// <summary>
        /// Creates and initializes the HUD using the UIDocument component.
        /// </summary>
        private void CreateHUD()
        {
            m_UIDocument = GetComponent<UIDocument>();
            if (m_UIDocument == null)
            {
                Debug.LogError("CoreHUD requires a UIDocument component.", this);
                return;
            }

            var root = m_UIDocument.rootVisualElement;
            CacheUIElements(root);
            ConfigureUIElements();
            ConfigureChallengeBetButtons();
            QueryHUDElements(root);
            SetHUDDefaults();
        }

        /// <summary>
        /// Caches references to common UI elements from the root visual element.
        /// </summary>
        /// <param name="root">The root visual element of the UIDocument.</param>
        private void CacheUIElements(VisualElement root)
        {
            m_MessageLabel = root.Q<Label>("message-label");
            m_CountdownLabel = root.Q<Label>("countdown-label");
            m_MessageOverlay = root.Q<VisualElement>("message-overlay");
            m_PlayerHealthBar = root.Q<ProgressBar>("player-health-bar");
            m_PlayerStaminaBar = root.Q<ProgressBar>("player-stamina-bar");
            m_NotificationContainer = root.Q<VisualElement>("notification-container");
            m_ScoreboardContainer = root.Q<VisualElement>("scoreboard-container");
            m_RoundTimerLabel = root.Q<Label>("round-timer-label");
            m_ChallengeOverlay = root.Q<VisualElement>("challenge-overlay");
            m_ChallengeTitleLabel = root.Q<Label>("challenge-title-label");
            m_ChallengeSubtitleLabel = root.Q<Label>("challenge-subtitle-label");
            m_ChallengeTimerBar = root.Q<ProgressBar>("challenge-timer-bar");
            m_ChallengeBetButtons = root.Q<VisualElement>("challenge-bet-buttons");
            m_ChallengeBetSuccessButton = root.Q<Button>("challenge-bet-success-button");
            m_ChallengeBetFailureButton = root.Q<Button>("challenge-bet-failure-button");
            m_ChallengeBetDeclineButton = root.Q<Button>("challenge-bet-decline-button");
            m_ChallengeBetStatusLabel = root.Q<Label>("challenge-bet-status-label");
            m_ChallengeBetAnnouncements = root.Q<VisualElement>("challenge-bet-announcements");
        }

        /// <summary>
        /// Configures the appearance and initial state of UI elements.
        /// </summary>
        private void ConfigureUIElements()
        {
            // Configure message overlay
            if (m_MessageOverlay != null)
            {
                m_MessageOverlay.style.display = DisplayStyle.None;
            }

            // Configure progress bar colors
            if (m_PlayerHealthBar != null)
            {
                m_PlayerHealthBarFill = m_PlayerHealthBar.Q<VisualElement>(null, "unity-progress-bar__progress");
                if (m_PlayerHealthBarFill != null)
                {
                    m_PlayerHealthBarFill.style.backgroundColor = k_HealthBarColor;
                }
            }

            if (m_PlayerStaminaBar != null)
            {
                m_PlayerStaminaBarFill = m_PlayerStaminaBar.Q<VisualElement>(null, "unity-progress-bar__progress");
                if (m_PlayerStaminaBarFill != null)
                {
                    m_PlayerStaminaBarFill.style.backgroundColor = k_StaminaBarColor;
                }
            }
        }

        /// <summary>
        /// Queries for additional HUD elements.
        /// </summary>
        /// <param name="root">The root visual element of the UIDocument.</param>
        protected virtual void QueryHUDElements(VisualElement root)
        {
            // Override in derived classes to query additional HUD elements
        }

        /// <summary>
        /// Sets default HUD states.
        /// </summary>
        protected virtual void SetHUDDefaults()
        {
            // Override in derived classes to set default HUD states
        }

        #endregion

        #region Notification System

        /// <summary>
        /// Adds a notification to the HUD notification system.
        /// </summary>
        /// <param name="message">The message text to display.</param>
        private void AddNotification(string message)
        {
            if (m_NotificationContainer == null) return;

            // Remove the oldest notification if at capacity
            if (m_ActiveNotifications.Count >= k_MaxNotifications)
            {
                RemoveNotification(m_ActiveNotifications[0]);
            }

            // Create notification elements
            var notificationElement = new VisualElement();
            notificationElement.AddToClassList("notification-item");

            var label = new Label(message);
            notificationElement.Add(label);

            // Create notification data
            var notificationData = new NotificationData
            {
                Element = notificationElement,
                Label = label,
                Message = message
            };

            // Add to UI and tracking
            m_NotificationContainer.Add(notificationElement);
            m_ActiveNotifications.Add(notificationData);

            // Start lifetime management
            notificationData.LifetimeCoroutine = StartCoroutine(NotificationLifetimeCoroutine(notificationData));
        }

        /// <summary>
        /// Manages the lifetime of a notification, including fade-out animation.
        /// </summary>
        /// <param name="notification">The notification data to manage.</param>
        /// <returns>IEnumerator for coroutine execution.</returns>
        private IEnumerator NotificationLifetimeCoroutine(NotificationData notification)
        {
            yield return new WaitForSeconds(k_NotificationDuration);

            // Fade out animation
            if (notification.Element != null)
            {
                notification.Element.AddToClassList("notification-fade-out");
                // Wait for fade animation
                yield return new WaitForSeconds(0.3f);
            }

            RemoveNotification(notification);
        }

        /// <summary>
        /// Removes a notification from the HUD and cleans up its resources.
        /// </summary>
        /// <param name="notification">The notification to remove.</param>
        private void RemoveNotification(NotificationData notification)
        {
            if (notification == null) return;

            if (notification.LifetimeCoroutine != null)
            {
                StopCoroutine(notification.LifetimeCoroutine);
            }

            if (notification.Element != null && m_NotificationContainer != null)
            {
                m_NotificationContainer.Remove(notification.Element);
            }

            m_ActiveNotifications.Remove(notification);
        }

        /// <summary>
        /// Clears all active notifications from the HUD.
        /// </summary>
        private void ClearAllNotifications()
        {
            var notificationsToRemove = new List<NotificationData>(m_ActiveNotifications);
            foreach (var notification in notificationsToRemove)
            {
                RemoveNotification(notification);
            }
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Validates that all required components are present.
        /// </summary>
        /// <returns>True if all components are valid, false otherwise.</returns>
        private bool ValidateComponents()
        {
            if (coreStats == null)
            {
                Debug.LogError("CoreHUD could not find CoreStatsHandler! The HUD will not function.", this);
                enabled = false;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Disables the HUD for non-owner clients.
        /// </summary>
        private void DisableForNonOwner()
        {
            if (TryGetComponent<UIDocument>(out var uiDoc))
            {
                uiDoc.enabled = false;
            }
            enabled = false;
        }

        /// <summary>
        /// Coroutine that performs initial HUD updates after spawn.
        /// </summary>
        /// <returns>IEnumerator for coroutine execution.</returns>
        private IEnumerator InitialHUDUpdate()
        {
            yield return null;

            if (coreStats != null)
            {
                foreach (var stat in coreStats.GetAllStats())
                {
                    HandleStatChanged(new StatChangePayload
                    {
                        statName = stat.name,
                        currentValue = stat.current,
                        maxValue = stat.max
                    });
                }
            }
        }

        /// <summary>
        /// Updates the local player's stat displays (health and stamina bars).
        /// </summary>
        /// <param name="payload">The stat change payload.</param>
        private void UpdateLocalPlayerStats(StatChangePayload payload)
        {
            if (payload.statID == StatKeys.Health)
            {
                if (m_PlayerHealthBar != null)
                {
                    m_PlayerHealthBar.highValue = payload.maxValue;
                    m_PlayerHealthBar.value = payload.currentValue;
                }
            }
            else if (payload.statID == StatKeys.Stamina)
            {
                if (m_PlayerStaminaBar != null)
                {
                    m_PlayerStaminaBar.highValue = payload.maxValue;
                    m_PlayerStaminaBar.value = payload.currentValue;
                }
            }
        }

        /// <summary>
        /// Processes elimination notifications when a player's health reaches zero.
        /// </summary>
        /// <param name="payload">The stat change payload to check for elimination.</param>
        private void ProcessEliminationNotification(StatChangePayload payload)
        {
            if (payload.statID == StatKeys.Health && payload.currentValue <= 0 && payload.sourceType == ModificationSource.Damage)
            {
                string sourcePlayerName = GetPlayerName(payload.sourcePlayerId);
                string targetPlayerName = GetPlayerName(payload.targetPlayerId);

                string eliminationMessage = (sourcePlayerName == targetPlayerName && payload.sourcePlayerId == payload.targetPlayerId)
                    ? $"{targetPlayerName} eliminated themselves"
                    : $"{sourcePlayerName} eliminated {targetPlayerName}";

                AddNotification(eliminationMessage);
            }
        }

        /// <summary>
        /// Gets a player name by ID, looking up from CorePlayerState.
        /// </summary>
        /// <param name="playerId">The player's network client ID.</param>
        /// <returns>The player's display name or a fallback format.</returns>
        private string GetPlayerName(ulong playerId)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.SpawnManager != null)
            {
                var playerObject = NetworkManager.Singleton.SpawnManager.GetPlayerNetworkObject(playerId);
                if (playerObject != null && playerObject.TryGetComponent<CorePlayerState>(out var playerState))
                {
                    string playerName = playerState.PlayerName;
                    if (!string.IsNullOrEmpty(playerName))
                    {
                        return playerName;
                    }
                }
            }
            return $"Player-{playerId}";
        }

        #endregion

        #region Scoreboard & Round Timer

        /// <summary>
        /// Called whenever any client connects or disconnects, to keep the scoreboard's rows in sync with who's
        /// actually in the game. Just rebuilds from scratch rather than tracking the specific client - simplest
        /// way to stay correct across joins/leaves, and cheap enough given expected player counts.
        /// </summary>
        /// <param name="clientId">The client that connected or disconnected (unused - a full rebuild covers both).</param>
        private void HandleScoreboardRosterChanged(ulong clientId)
        {
            RequestScoreboardRefresh();
        }

        /// <summary>
        /// Kicks off (or restarts) the retrying scoreboard refresh - see RefreshScoreboardUntilComplete for why
        /// a single RefreshScoreboard() call isn't reliable right after a connect event.
        /// </summary>
        private void RequestScoreboardRefresh()
        {
            if (m_ScoreboardRefreshCoroutine != null)
            {
                StopCoroutine(m_ScoreboardRefreshCoroutine);
            }
            m_ScoreboardRefreshCoroutine = StartCoroutine(RefreshScoreboardUntilComplete());
        }

        /// <summary>
        /// A newly-connected client's player object doesn't always exist yet the instant
        /// OnClientConnectedCallback fires - their scene sync / player-prefab spawn can still be in
        /// flight for a frame or more. RefreshScoreboard() silently skips any connected client it can't
        /// find a player object for, and since nothing else re-triggers a rebuild until the NEXT connect
        /// or disconnect, a client whose object wasn't ready yet could simply never get a row. Retry for
        /// a few seconds instead of assuming one attempt is enough.
        /// </summary>
        private IEnumerator RefreshScoreboardUntilComplete()
        {
            const int maxAttempts = 20;
            const float retryDelaySeconds = 0.25f;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                RefreshScoreboard();

                if (NetworkManager.Singleton == null) yield break;

                bool everyoneHasARow = true;
                foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
                {
                    if (!m_ScoreboardRows.ContainsKey(clientId))
                    {
                        everyoneHasARow = false;
                        break;
                    }
                }

                if (everyoneHasARow) yield break;
                yield return new WaitForSeconds(retryDelaySeconds);
            }
        }

        /// <summary>
        /// Rebuilds the scoreboard against the current connected-clients list: one row per connected player,
        /// showing their name (see <see cref="GetPlayerName"/>) and their live, server-authoritative score.
        /// </summary>
        private void RefreshScoreboard()
        {
            if (m_ScoreboardContainer == null || NetworkManager.Singleton == null) return;

            ClearScoreboardRows();

            foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
            {
                var playerObject = NetworkManager.Singleton.SpawnManager.GetPlayerNetworkObject(clientId);
                if (playerObject == null || !playerObject.TryGetComponent<PlayerScore>(out var playerScore)) continue;

                var row = new VisualElement();
                row.AddToClassList("scoreboard-row");

                var nameLabel = new Label(GetPlayerName(clientId));
                nameLabel.AddToClassList("scoreboard-name");
                row.Add(nameLabel);

                var scoreLabel = new Label(playerScore.Score.Value.ToString());
                scoreLabel.AddToClassList("scoreboard-score");
                row.Add(scoreLabel);

                m_ScoreboardContainer.Add(row);

                var scoreboardRow = new ScoreboardRow
                {
                    Element = row,
                    ScoreLabel = scoreLabel,
                    PlayerScoreComponent = playerScore
                };
                playerScore.Score.OnValueChanged += scoreboardRow.HandleScoreChanged;
                m_ScoreboardRows[clientId] = scoreboardRow;
            }
        }

        /// <summary>
        /// Unsubscribes and removes every currently-displayed scoreboard row.
        /// </summary>
        private void ClearScoreboardRows()
        {
            foreach (var row in m_ScoreboardRows.Values)
            {
                if (row.PlayerScoreComponent != null)
                {
                    row.PlayerScoreComponent.Score.OnValueChanged -= row.HandleScoreChanged;
                }
                m_ScoreboardContainer?.Remove(row.Element);
            }
            m_ScoreboardRows.Clear();
        }

        /// <summary>
        /// Updates the round timer label whenever the server-authoritative countdown changes.
        /// </summary>
        private void HandleRoundTimerChanged(float previousValue, float newValue)
        {
            UpdateTimerLabel(newValue);
        }

        /// <summary>
        /// Formats and applies the given number of seconds remaining to the round timer label, as M:SS.
        /// </summary>
        private void UpdateTimerLabel(float secondsRemaining)
        {
            if (m_RoundTimerLabel == null) return;

            int totalSeconds = Mathf.Max(0, Mathf.CeilToInt(secondsRemaining));
            m_RoundTimerLabel.text = $"{totalSeconds / 60}:{totalSeconds % 60:00}";
        }

        #endregion

        #region Challenge Betting Overlay

        /// <summary>
        /// Wires the three betting buttons once, at HUD creation - unlike the scoreboard's rows, these are
        /// static UI elements that just get shown/hidden, never rebuilt.
        /// </summary>
        private void ConfigureChallengeBetButtons()
        {
            if (m_ChallengeBetSuccessButton != null) m_ChallengeBetSuccessButton.clicked += () => PlaceBet(ChallengeBetChoice.Success);
            if (m_ChallengeBetFailureButton != null) m_ChallengeBetFailureButton.clicked += () => PlaceBet(ChallengeBetChoice.Failure);
            if (m_ChallengeBetDeclineButton != null) m_ChallengeBetDeclineButton.clicked += () => PlaceBet(ChallengeBetChoice.Decline);
        }

        /// <summary>
        /// Sends this player's bet to the server and updates this client's own view immediately - the
        /// server is the actual source of truth (see ChallengeManager.PlaceBetRpc), this is just so the
        /// buttons don't linger after being clicked while waiting for the round trip.
        /// </summary>
        private void PlaceBet(ChallengeBetChoice choice)
        {
            if (ChallengeManager.Instance == null) return;

            m_HasPlacedBetThisWindow = true;
            ChallengeManager.Instance.PlaceBetRpc(choice);

            UpdateChallengeBetControlsVisibility();
            if (m_ChallengeBetStatusLabel != null)
            {
                m_ChallengeBetStatusLabel.style.display = DisplayStyle.Flex;
                m_ChallengeBetStatusLabel.text = choice == ChallengeBetChoice.Decline
                    ? "You declined to bet."
                    : $"You bet: {DescribeBetChoice(choice)}";
            }
        }

        /// <summary>
        /// Shows or hides the whole betting overlay when a betting window opens or closes, and refreshes
        /// its contents (challenge name, challenger, timer, bet controls, announcements) while it's open.
        /// </summary>
        private void HandleBettingWindowActiveChanged(bool previousValue, bool isActive)
        {
            if (isActive && m_ChallengeBetStatusLabel != null)
            {
                m_ChallengeBetStatusLabel.style.display = DisplayStyle.None;
            }

            // GameManager locks and hides the cursor for normal gameplay (mouse-look movement). That's
            // exactly what made the betting buttons uninteractable: a locked cursor doesn't move around
            // the screen, so a click always lands wherever the (invisible) cursor already was - never on
            // a button. Give the cursor back while the overlay is up, and re-lock it once betting closes
            // so movement/look return to normal. Fully qualified because UnityEngine.UIElements (used
            // throughout this file) also declares its own Cursor type for USS cursor styling.
            UnityEngine.Cursor.lockState = isActive ? CursorLockMode.None : CursorLockMode.Locked;
            UnityEngine.Cursor.visible = isActive;

            RefreshChallengeOverlay();
        }

        /// <summary>
        /// Updates the countdown bar as the server ticks it down.
        /// </summary>
        private void HandleBettingTimeRemainingChanged(float previousValue, float newValue)
        {
            if (m_ChallengeTimerBar != null)
            {
                m_ChallengeTimerBar.value = newValue;
            }
        }

        /// <summary>
        /// Rebuilds the "so-and-so has bet" announcements whenever the server's bettor list changes.
        /// </summary>
        private void HandleActiveBettorsChanged(NetworkListEvent<ulong> changeEvent)
        {
            RefreshBettorAnnouncements();
        }

        /// <summary>
        /// Refreshes every part of the betting overlay against ChallengeManager's current state. Safe to
        /// call at any time - a fresh window resets <see cref="m_HasPlacedBetThisWindow"/> via
        /// HandleBettingWindowActiveChanged before this runs, so re-entering an already-open window (e.g.
        /// on late join) won't wrongly show buttons to someone who already bet.
        /// </summary>
        private void RefreshChallengeOverlay()
        {
            if (m_ChallengeOverlay == null || ChallengeManager.Instance == null) return;

            bool isActive = ChallengeManager.Instance.IsBettingWindowActive.Value;
            m_ChallengeOverlay.style.display = isActive ? DisplayStyle.Flex : DisplayStyle.None;
            if (!isActive) return;

            string challengeName = ChallengeManager.Instance.ActiveChallengeName.Value.ToString();
            ulong challengerId = ChallengeManager.Instance.ActiveChallengerClientId.Value;

            if (m_ChallengeTitleLabel != null) m_ChallengeTitleLabel.text = challengeName;
            if (m_ChallengeSubtitleLabel != null) m_ChallengeSubtitleLabel.text = $"{GetPlayerName(challengerId)} is attempting this challenge";
            if (m_ChallengeTimerBar != null)
            {
                m_ChallengeTimerBar.highValue = ChallengeManager.Instance.BettingWindowSeconds;
                m_ChallengeTimerBar.value = ChallengeManager.Instance.BettingTimeRemaining.Value;
            }

            UpdateChallengeBetControlsVisibility();
            RefreshBettorAnnouncements();
        }

        /// <summary>
        /// The challenging player never sees the betting buttons (only the countdown), and once anyone has
        /// placed their bet for this window, the buttons hide for them too.
        /// </summary>
        private void UpdateChallengeBetControlsVisibility()
        {
            if (m_ChallengeBetButtons == null || ChallengeManager.Instance == null) return;

            bool isLocalPlayerTheChallenger = NetworkManager.Singleton != null &&
                NetworkManager.Singleton.LocalClientId == ChallengeManager.Instance.ActiveChallengerClientId.Value;
            bool showButtons = !isLocalPlayerTheChallenger && !m_HasPlacedBetThisWindow;
            m_ChallengeBetButtons.style.display = showButtons ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// Rebuilds the "[Player Name] has bet on the outcome of [Challenge Name]" rows. Doesn't reveal
        /// which of the three options anyone picked - just that they wagered.
        /// </summary>
        private void RefreshBettorAnnouncements()
        {
            if (m_ChallengeBetAnnouncements == null || ChallengeManager.Instance == null) return;

            m_ChallengeBetAnnouncements.Clear();
            string challengeName = ChallengeManager.Instance.ActiveChallengeName.Value.ToString();

            foreach (ulong bettorId in ChallengeManager.Instance.ActiveBettorClientIds)
            {
                var label = new Label($"{GetPlayerName(bettorId)} has bet on the outcome of {challengeName}");
                label.AddToClassList("challenge-bet-announcement");
                m_ChallengeBetAnnouncements.Add(label);
            }
        }

        private static string DescribeBetChoice(ChallengeBetChoice choice)
        {
            return choice switch
            {
                ChallengeBetChoice.Success => "He succeeds in under both Pars",
                ChallengeBetChoice.Failure => "He fails to achieve both pars",
                _ => "Declined"
            };
        }

        #endregion
    }
}
