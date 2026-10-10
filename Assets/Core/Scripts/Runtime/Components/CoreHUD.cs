using UnityEngine;
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
    public class CoreHUD : CoreNetworkBehaviour
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
        private Label m_RoundTimerCaption;
        private Label m_BonusTimerLabel;
        private VisualElement m_BonusTimerContainer;
        private VisualElement m_RoundBanner;
        private Label m_RoundBannerTitle;
        private Label m_RoundBannerSubtitle;
        private Coroutine m_BannerHideCoroutine;
        private Label m_TotalScoreLabel;
        private Label m_TargetScoreLabel;
        private InGameMenu m_InGameMenu;

        // Small stamina bar floating above the player's head (the bottom-left bars are hidden - see BuildStaminaBar).
        private VisualElement m_StaminaFloat;
        private VisualElement m_StaminaFloatFill;
        private float m_StaminaFullSince = -1f;
        private const float k_StaminaFloatWidth = 64f;
        private const float k_StaminaFloatHeight = 6f;
        private const float k_StaminaHideDelay = 0.6f;
        private WagerHUD m_WagerHUD;
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

        // Scene singletons this HUD subscribed to (kept so the exact same instances are unsubscribed)
        private RoundTimer m_SubscribedRoundTimer;
        private ChallengeManager m_SubscribedChallengeManager;

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
        /// player's PlayerScore it's bound to (so its ScoreChanged subscription can be cleanly removed later).
        /// </summary>
        private class ScoreboardRow
        {
            public VisualElement Element;
            public Label ScoreLabel;
            public PlayerScore PlayerScoreComponent;
            public System.Action AnyScoreChanged;

            public void HandleScoreChanged(int previousValue, int newValue)
            {
                if (ScoreLabel != null)
                {
                    ScoreLabel.text = newValue.ToString();
                }
                AnyScoreChanged?.Invoke();
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

            NetworkPlayers.RosterChanged += HandleScoreboardRosterChanged;
            SessionProfiles.Changed += HandleScoreboardRosterChanged; // names can arrive after the avatars
            RequestScoreboardRefresh();

            m_SubscribedRoundTimer = RoundTimer.Instance;
            if (m_SubscribedRoundTimer != null)
            {
                m_SubscribedRoundTimer.TimeRemainingChanged += HandleRoundTimerChanged;
                m_SubscribedRoundTimer.BonusTimeRemainingChanged += HandleBonusTimerChanged;
                m_SubscribedRoundTimer.PhaseChanged += HandleRoundPhaseChanged;
                m_SubscribedRoundTimer.FinishedPlayersChanged += HandleFinishedPlayersChanged;
                m_SubscribedRoundTimer.RoundInfoChanged += HandleRoundInfoChanged;
                m_SubscribedRoundTimer.PlayerFinished += HandlePlayerFinished;
                UpdateTimerLabel(m_SubscribedRoundTimer.TimeRemaining);
                UpdateBonusTimerLabel(m_SubscribedRoundTimer.BonusTimeRemaining);
                HandleRoundInfoChanged();
                if (m_SubscribedRoundTimer.IsSpawned && m_SubscribedRoundTimer.Phase != RoundTimer.RoundPhase.Playing)
                {
                    HandleRoundPhaseChanged(m_SubscribedRoundTimer.Phase);
                }
            }

            // Escape menu (Settings / Quit to Main Menu / Quit Game / Return to Game), drawn on this HUD's panel.
            if (m_UIDocument != null)
            {
                m_InGameMenu = gameObject.AddComponent<InGameMenu>();
                m_InGameMenu.Initialize(m_UIDocument.rootVisualElement);

                // Wager ticker (top) and this player's phone of active bets (bottom right) - not in practice.
                if (!PracticeMode.IsActive)
                {
                    m_WagerHUD = gameObject.AddComponent<WagerHUD>();
                    m_WagerHUD.Initialize(m_UIDocument.rootVisualElement, AddNotification);
                }
            }

            if (PracticeMode.IsActive)
            {
                SetUpPracticeTimer();
            }

            m_SubscribedChallengeManager = ChallengeManager.Instance;
            if (m_SubscribedChallengeManager != null)
            {
                m_SubscribedChallengeManager.BettingWindowActiveChanged += HandleBettingWindowActiveChanged;
                m_SubscribedChallengeManager.BettingTimeRemainingChanged += HandleBettingTimeRemainingChanged;
                m_SubscribedChallengeManager.ActiveBettorsChanged += HandleActiveBettorsChanged;
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

                NetworkPlayers.RosterChanged -= HandleScoreboardRosterChanged;
                SessionProfiles.Changed -= HandleScoreboardRosterChanged;

                if (m_SubscribedRoundTimer != null)
                {
                    m_SubscribedRoundTimer.TimeRemainingChanged -= HandleRoundTimerChanged;
                    m_SubscribedRoundTimer.BonusTimeRemainingChanged -= HandleBonusTimerChanged;
                    m_SubscribedRoundTimer.PhaseChanged -= HandleRoundPhaseChanged;
                    m_SubscribedRoundTimer.FinishedPlayersChanged -= HandleFinishedPlayersChanged;
                    m_SubscribedRoundTimer.RoundInfoChanged -= HandleRoundInfoChanged;
                    m_SubscribedRoundTimer.PlayerFinished -= HandlePlayerFinished;
                    m_SubscribedRoundTimer = null;
                }

                if (m_InGameMenu != null)
                {
                    Destroy(m_InGameMenu);
                    m_InGameMenu = null;
                }

                if (m_WagerHUD != null)
                {
                    Destroy(m_WagerHUD);
                    m_WagerHUD = null;
                }

                if (m_SubscribedChallengeManager != null)
                {
                    m_SubscribedChallengeManager.BettingWindowActiveChanged -= HandleBettingWindowActiveChanged;
                    m_SubscribedChallengeManager.BettingTimeRemainingChanged -= HandleBettingTimeRemainingChanged;
                    m_SubscribedChallengeManager.ActiveBettorsChanged -= HandleActiveBettorsChanged;
                    m_SubscribedChallengeManager = null;
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
            BuildStaminaBar(root);
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
            m_RoundTimerCaption = root.Q<Label>("round-timer-caption");
            m_BonusTimerLabel = root.Q<Label>("bonus-timer-label");
            m_BonusTimerContainer = root.Q<VisualElement>("bonus-timer-container");
            m_RoundBanner = root.Q<VisualElement>("round-banner");
            m_RoundBannerTitle = root.Q<Label>("round-banner-title");
            m_RoundBannerSubtitle = root.Q<Label>("round-banner-subtitle");
            m_ChallengeOverlay = root.Q<VisualElement>("challenge-overlay");
            m_ChallengeTitleLabel = root.Q<Label>("challenge-title-label");
            m_ChallengeSubtitleLabel = root.Q<Label>("challenge-subtitle-label");
            m_ChallengeTimerBar = root.Q<ProgressBar>("challenge-timer-bar");
            m_ChallengeBetButtons = root.Q<VisualElement>("challenge-bet-buttons");
            m_ChallengeBetSuccessButton = root.Q<Button>("challenge-bet-success-button");
            m_ChallengeBetFailureButton = root.Q<Button>("challenge-bet-failure-button");
            m_ChallengeBetDeclineButton = root.Q<Button>("challenge-bet-decline-button");
            MenuUI.ApplyButtonFont(m_ChallengeBetSuccessButton);
            MenuUI.ApplyButtonFont(m_ChallengeBetFailureButton);
            MenuUI.ApplyButtonFont(m_ChallengeBetDeclineButton);
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

        #region Floating Stamina Bar

        /// <summary>
        /// Hides the old bottom-left health/stamina bars (health isn't a gameplay concept right now - it's still
        /// tracked in the back end, and the bars still get updated, for hazards later) and adds a small
        /// stamina bar that floats just above the player's head, shown only while stamina isn't full.
        /// </summary>
        private void BuildStaminaBar(VisualElement root)
        {
            var oldBars = root.Q<VisualElement>("health-info-container");
            if (oldBars != null) oldBars.style.display = DisplayStyle.None;

            m_StaminaFloat = new VisualElement { name = "stamina-float", pickingMode = PickingMode.Ignore };
            m_StaminaFloat.style.position = Position.Absolute;
            m_StaminaFloat.style.width = k_StaminaFloatWidth;
            m_StaminaFloat.style.height = k_StaminaFloatHeight;
            m_StaminaFloat.style.backgroundColor = new Color(0.06f, 0.06f, 0.06f, 0.7f);
            m_StaminaFloat.style.borderTopLeftRadius = m_StaminaFloat.style.borderTopRightRadius = 3;
            m_StaminaFloat.style.borderBottomLeftRadius = m_StaminaFloat.style.borderBottomRightRadius = 3;
            m_StaminaFloat.style.overflow = Overflow.Hidden;
            m_StaminaFloat.style.display = DisplayStyle.None;

            m_StaminaFloatFill = new VisualElement { pickingMode = PickingMode.Ignore };
            m_StaminaFloatFill.style.height = Length.Percent(100);
            m_StaminaFloatFill.style.width = Length.Percent(100);
            m_StaminaFloatFill.style.backgroundColor = new Color(k_StaminaBarColor.r, k_StaminaBarColor.g, k_StaminaBarColor.b, 0.95f);
            m_StaminaFloat.Add(m_StaminaFloatFill);

            root.Add(m_StaminaFloat);
        }

        private void UpdateStaminaFloat(float current, float max)
        {
            if (m_StaminaFloat == null || max <= 0f) return;

            float fraction = Mathf.Clamp01(current / max);
            m_StaminaFloatFill.style.width = Length.Percent(fraction * 100f);

            if (fraction >= 0.999f)
            {
                if (m_StaminaFullSince < 0f) m_StaminaFullSince = Time.time; // hidden shortly after it refills
            }
            else
            {
                m_StaminaFullSince = -1f;
                m_StaminaFloat.style.display = DisplayStyle.Flex;
            }
        }

        /// <summary>Keeps the stamina bar just above the player's head on screen, and hides it once stamina has been full for a moment.</summary>
        private void LateUpdate()
        {
            if (!IsOwner || m_StaminaFloat == null) return;

            if (m_StaminaFullSince >= 0f && Time.time - m_StaminaFullSince >= k_StaminaHideDelay)
            {
                m_StaminaFloat.style.display = DisplayStyle.None;
            }
            if (m_StaminaFloat.resolvedStyle.display == DisplayStyle.None && m_StaminaFloat.style.display == DisplayStyle.None) return;

            Camera camera = Camera.main;
            IPanel panel = m_StaminaFloat.panel;
            if (camera == null || panel == null) return;

            float headHeight = 2f;
            if (TryGetComponent(out CharacterController controller)) headHeight = controller.center.y + controller.height * 0.5f;
            Vector3 head = transform.position + Vector3.up * (headHeight + 0.35f);

            if (camera.WorldToViewportPoint(head).z <= 0f)
            {
                m_StaminaFloat.style.visibility = Visibility.Hidden;
                return;
            }

            Vector2 point = RuntimePanelUtils.CameraTransformWorldToPanel(panel, head, camera);
            m_StaminaFloat.style.visibility = Visibility.Visible;
            m_StaminaFloat.style.left = point.x - k_StaminaFloatWidth * 0.5f;
            m_StaminaFloat.style.top = point.y - k_StaminaFloatHeight;
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
                UpdateStaminaFloat(payload.currentValue, payload.maxValue);
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
        /// <param name="playerId">The player's id (see NetworkPlayers).</param>
        /// <returns>The player's display name or a fallback format.</returns>
        private string GetPlayerName(ulong playerId)
        {
            return NetworkPlayers.GetDisplayName(playerId);
        }

        #endregion

        #region Scoreboard & Round Timer

        /// <summary>
        /// Called whenever any player avatar spawns or despawns (NetworkPlayers.RosterChanged), to keep the
        /// scoreboard's rows in sync with who's actually in the game. Just rebuilds from scratch rather than
        /// tracking the specific player - simplest way to stay correct across joins/leaves, and cheap enough
        /// given expected player counts.
        /// </summary>
        private void HandleScoreboardRosterChanged()
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

                bool everyoneHasARow = true;
                foreach (ulong clientId in NetworkPlayers.Ids)
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
            if (m_ScoreboardContainer == null) return;

            ClearScoreboardRows();

            // Semi-cooperative: the team's total (and the target it's chasing) sit above everyone's own score.
            // Practice has no target - just the player's own (pickup) score.
            if (!PracticeMode.IsActive)
            {
                m_TotalScoreLabel = AddTeamRow("TOTAL", "scoreboard-total-score");
                m_TargetScoreLabel = AddTeamRow("TARGET", "scoreboard-target-score");
                var divider = new VisualElement();
                divider.AddToClassList("scoreboard-divider");
                m_ScoreboardContainer.Add(divider);
            }

            foreach (ulong clientId in NetworkPlayers.Ids)
            {
                if (!NetworkPlayers.TryGetComponent(clientId, out PlayerScore playerScore)) continue;

                var row = new VisualElement();
                row.AddToClassList("scoreboard-row");

                bool finished = RoundTimer.Instance != null && RoundTimer.Instance.IsPlayerFinished(clientId);
                var nameLabel = new Label(NetworkPlayers.GetPlayerLabel(clientId) + (finished ? " (finished)" : ""));
                nameLabel.AddToClassList("scoreboard-name");
                if (finished) nameLabel.AddToClassList("scoreboard-name--finished");
                row.Add(nameLabel);

                var scoreLabel = new Label(playerScore.Score.ToString());
                scoreLabel.AddToClassList("scoreboard-score");
                row.Add(scoreLabel);

                m_ScoreboardContainer.Add(row);

                var scoreboardRow = new ScoreboardRow
                {
                    Element = row,
                    ScoreLabel = scoreLabel,
                    PlayerScoreComponent = playerScore,
                    AnyScoreChanged = UpdateTeamRows
                };
                playerScore.ScoreChanged += scoreboardRow.HandleScoreChanged;
                m_ScoreboardRows[clientId] = scoreboardRow;
            }

            UpdateTeamRows();
        }

        /// <summary>One of the TOTAL / TARGET rows at the top of the scoreboard. Returns its value label.</summary>
        private Label AddTeamRow(string title, string scoreClass)
        {
            var row = new VisualElement();
            row.AddToClassList("scoreboard-row");
            row.AddToClassList("scoreboard-team-row");

            var nameLabel = new Label(title);
            nameLabel.AddToClassList("scoreboard-name");
            row.Add(nameLabel);

            var valueLabel = new Label("0");
            valueLabel.AddToClassList("scoreboard-score");
            valueLabel.AddToClassList(scoreClass);
            row.Add(valueLabel);

            m_ScoreboardContainer.Add(row);
            return valueLabel;
        }

        /// <summary>Re-adds everyone's score into TOTAL and refreshes TARGET (green once the team has reached it).</summary>
        private void UpdateTeamRows()
        {
            int total = RoundTimer.ComputeTeamTotal();
            int target = RoundTimer.Instance != null ? RoundTimer.Instance.TeamTarget : 0;

            if (m_TotalScoreLabel != null) m_TotalScoreLabel.text = total.ToString();
            if (m_TargetScoreLabel != null)
            {
                m_TargetScoreLabel.text = target > 0 ? target.ToString() : "-";
                bool met = target > 0 && total >= target;
                m_TargetScoreLabel.EnableInClassList("scoreboard-target-met", met);
                m_TotalScoreLabel?.EnableInClassList("scoreboard-target-met", met);
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
                    row.PlayerScoreComponent.ScoreChanged -= row.HandleScoreChanged;
                }
                m_ScoreboardContainer?.Remove(row.Element);
            }
            m_ScoreboardRows.Clear();
            m_ScoreboardContainer?.Clear();
            m_TotalScoreLabel = null;
            m_TargetScoreLabel = null;
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
            if (m_RoundTimerLabel == null || PracticeMode.IsActive) return;
            m_RoundTimerLabel.text = FormatTime(secondsRemaining);
        }

        private void HandleBonusTimerChanged(float previousValue, float newValue)
        {
            UpdateBonusTimerLabel(newValue);
        }

        /// <summary>The bonus timer, dimmed once it has run out (finishing then earns no bonus).</summary>
        private void UpdateBonusTimerLabel(float secondsRemaining)
        {
            if (PracticeMode.IsActive) return;
            if (m_BonusTimerLabel != null) m_BonusTimerLabel.text = FormatTime(secondsRemaining);
            m_BonusTimerContainer?.EnableInClassList("hud-timer--expired", Mathf.CeilToInt(secondsRemaining) <= 0);
        }

        private static string FormatTime(float secondsRemaining)
        {
            int totalSeconds = Mathf.Max(0, Mathf.CeilToInt(secondsRemaining));
            return $"{totalSeconds / 60}:{totalSeconds % 60:00}";
        }

        /// <summary>
        /// Practice has no time limit: the bonus box goes away and the round box becomes a "PRACTICE"
        /// stopwatch counting up from when the player arrived, handy for timing routes.
        /// </summary>
        private void SetUpPracticeTimer()
        {
            if (m_BonusTimerContainer != null) m_BonusTimerContainer.style.display = DisplayStyle.None;
            if (m_RoundTimerCaption != null) m_RoundTimerCaption.text = "PRACTICE";
            if (m_RoundTimerLabel == null) return;

            float start = Time.time;
            m_RoundTimerLabel.text = "0:00";
            m_RoundTimerLabel.schedule.Execute(() =>
            {
                int seconds = Mathf.FloorToInt(Time.time - start);
                m_RoundTimerLabel.text = $"{seconds / 60}:{seconds % 60:00}";
            }).Every(250);
        }

        private void HandleRoundInfoChanged()
        {
            if (PracticeMode.IsActive) { UpdateTeamRows(); return; }
            if (m_RoundTimerCaption != null && m_SubscribedRoundTimer != null)
            {
                m_RoundTimerCaption.text = $"ROUND {m_SubscribedRoundTimer.RoundNumber}";
            }
            UpdateTeamRows();
        }

        private void HandleFinishedPlayersChanged()
        {
            RequestScoreboardRefresh(); // "(finished)" markers
            UpdateChallengeBetControlsVisibility(); // a finished player can't wager any more
        }

        /// <summary>Someone reached the end point: a banner for this player, a notification for anyone else.</summary>
        private void HandlePlayerFinished(ulong playerId, int bonusPoints)
        {
            if (playerId == OwnerClientId)
            {
                ShowBanner("FINISHED!", bonusPoints > 0 ? $"+{bonusPoints} bonus points" : "No time left on the bonus timer", null, 3f);
            }
            else
            {
                AddNotification(bonusPoints > 0
                    ? $"{GetPlayerName(playerId)} reached the end! +{bonusPoints}"
                    : $"{GetPlayerName(playerId)} reached the end!");
            }
        }

        private void HandleRoundPhaseChanged(RoundTimer.RoundPhase phase)
        {
            UpdateChallengeBetControlsVisibility();
            if (m_SubscribedRoundTimer == null) return;

            switch (phase)
            {
                case RoundTimer.RoundPhase.Tallying:
                    ShowBanner("ROUND OVER", "Adding up the scores...", null, 0f);
                    break;

                case RoundTimer.RoundPhase.Ended:
                    bool reached = m_SubscribedRoundTimer.TargetReached;
                    string score = $"Team scored {m_SubscribedRoundTimer.FinalTeamTotal} of {m_SubscribedRoundTimer.TeamTarget}";
                    ShowBanner(reached ? "TARGET REACHED!" : "TARGET MISSED", score, reached, 0f);
                    UpdateTeamRows();
                    break;
            }
        }

        /// <summary>Shows the upper-middle banner; hides it again after <paramref name="hideAfterSeconds"/> (0 = keep it).</summary>
        private void ShowBanner(string title, string subtitle, bool? good, float hideAfterSeconds)
        {
            if (m_RoundBanner == null) return;

            if (m_BannerHideCoroutine != null)
            {
                StopCoroutine(m_BannerHideCoroutine);
                m_BannerHideCoroutine = null;
            }

            if (m_RoundBannerTitle != null) m_RoundBannerTitle.text = title;
            if (m_RoundBannerSubtitle != null) m_RoundBannerSubtitle.text = subtitle ?? "";
            m_RoundBanner.EnableInClassList("round-banner--good", good == true);
            m_RoundBanner.EnableInClassList("round-banner--bad", good == false);
            m_RoundBanner.style.display = DisplayStyle.Flex;

            if (hideAfterSeconds > 0f)
            {
                m_BannerHideCoroutine = StartCoroutine(HideBannerAfter(hideAfterSeconds));
            }
        }

        private IEnumerator HideBannerAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            m_BannerHideCoroutine = null;
            if (m_SubscribedRoundTimer != null && m_SubscribedRoundTimer.Phase != RoundTimer.RoundPhase.Playing) yield break;
            if (m_RoundBanner != null) m_RoundBanner.style.display = DisplayStyle.None;
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
            ChallengeManager.Instance.PlaceBet(choice);

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
            if (isActive)
            {
                // A new window - this player hasn't bet in it yet (see RefreshChallengeOverlay).
                m_HasPlacedBetThisWindow = false;
            }

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
            if (!InGameMenu.IsOpen) // the Escape menu owns the cursor while it's open
            {
                UnityEngine.Cursor.lockState = isActive ? CursorLockMode.None : CursorLockMode.Locked;
                UnityEngine.Cursor.visible = isActive;
            }

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
        private void HandleActiveBettorsChanged()
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

            bool isActive = ChallengeManager.Instance.IsBettingWindowActive;
            m_ChallengeOverlay.style.display = isActive ? DisplayStyle.Flex : DisplayStyle.None;
            if (!isActive) return;

            string challengeName = ChallengeManager.Instance.ActiveChallengeName;
            ulong challengerId = ChallengeManager.Instance.ActiveChallengerClientId;

            if (m_ChallengeTitleLabel != null) m_ChallengeTitleLabel.text = challengeName;
            if (m_ChallengeSubtitleLabel != null) m_ChallengeSubtitleLabel.text = $"{GetPlayerName(challengerId)} is attempting this challenge";
            if (m_ChallengeTimerBar != null)
            {
                m_ChallengeTimerBar.highValue = ChallengeManager.Instance.BettingWindowSeconds;
                m_ChallengeTimerBar.value = ChallengeManager.Instance.BettingTimeRemaining;
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

            bool isLocalPlayerTheChallenger = OwnerClientId == ChallengeManager.Instance.ActiveChallengerClientId;
            bool canWager = ChallengeManager.IsRoundOpenFor(OwnerClientId); // not once you've reached the end point
            bool showButtons = !isLocalPlayerTheChallenger && !m_HasPlacedBetThisWindow && canWager;
            m_ChallengeBetButtons.style.display = showButtons ? DisplayStyle.Flex : DisplayStyle.None;

            if (!canWager && !isLocalPlayerTheChallenger && !m_HasPlacedBetThisWindow && m_ChallengeBetStatusLabel != null
                && ChallengeManager.Instance.IsBettingWindowActive)
            {
                m_ChallengeBetStatusLabel.style.display = DisplayStyle.Flex;
                m_ChallengeBetStatusLabel.text = "You've finished this round - no more wagers.";
            }
        }

        /// <summary>
        /// Rebuilds the "[Player Name] has bet on the outcome of [Challenge Name]" rows. Doesn't reveal
        /// which of the three options anyone picked - just that they wagered.
        /// </summary>
        private void RefreshBettorAnnouncements()
        {
            if (m_ChallengeBetAnnouncements == null || ChallengeManager.Instance == null) return;

            m_ChallengeBetAnnouncements.Clear();
            string challengeName = ChallengeManager.Instance.ActiveChallengeName;

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
