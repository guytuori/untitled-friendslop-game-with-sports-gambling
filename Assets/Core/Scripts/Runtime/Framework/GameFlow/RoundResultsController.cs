using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The between-rounds screen, loaded for everyone (by the master client's RoundTimer) when the team hits
    /// a round's target. A placeholder for now: the round's scores (from MatchProgress, recorded by every
    /// client as the round ended), the team total against the target, the next round's target, and
    /// "Press any key to go to the next round".
    ///
    /// Pressing a key marks this player ready (MatchFlowSync.ReadyRpc, a static RPC - this scene has no
    /// networked objects). Once every player in the session is ready, the master client loads the gameplay
    /// scene again: same map, freshly randomized challenges, a higher target (Target Score Growth), and
    /// everyone starting from Starting Points plus 5% of their final score (see PlayerScore).
    ///
    /// ITEM SHOP (when items are on): each player is offered three items, by how they placed last round (see
    /// ItemCatalog.RollShop), paid for out of last round's score and taken off what they start the next round
    /// with. Choosing an item or NO ITEM is what marks them ready (instead of any key); the item is in their
    /// slot when the round starts (MatchProgress.LocalShopItem / LocalShopSpend).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class RoundResultsController : MonoBehaviour
    {
        [Tooltip("Same logo as the other GameFlow screens.")]
        [SerializeField] private Texture2D titleImage;

        [Tooltip("Scene to go to if the connection is lost.")]
        [SerializeField] private string exitSceneName = "MainMenu";

        [Tooltip("Played when this player presses a key to get ready.")]
        [SerializeField] private AudioClip selectSound;

        [Tooltip("Seconds before key presses count, so a button still held from the round doesn't skip the screen.")]
        [SerializeField] private float inputDelaySeconds = 1f;

        [SerializeField] private float promptFlashInterval = 0.5f;

        private Label m_Prompt;
        private Label m_ReadyLabel;
        private bool m_Ready;
        private bool m_Loading;
        private bool m_Exiting;
        private float m_ShownTime;
        private bool m_ShopMode;
        private ItemShopPanel m_Shop;

        private static NetworkRunner Runner => FusionSessionService.HasInstance ? FusionSessionService.Instance.GameRunner : null;

        private void Awake()
        {
            GamepadUIBindingFix.Apply();
            m_ShownTime = Time.unscaledTime;

            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            BuildUI(GetComponent<UIDocument>().rootVisualElement);
        }

        private void OnEnable()
        {
            MatchProgress.ReadyChanged += RefreshReady;
            if (FusionSessionService.HasInstance) FusionSessionService.Instance.GameEnded += HandleGameEnded;
            StartCoroutine(FlashPrompt());
        }

        private void OnDisable()
        {
            MatchProgress.ReadyChanged -= RefreshReady;
            if (FusionSessionService.HasInstance) FusionSessionService.Instance.GameEnded -= HandleGameEnded;
        }

        private void BuildUI(VisualElement root)
        {
            MenuUI.SetupRoot(root);
            root.style.justifyContent = Justify.FlexStart;
            MenuUI.AddSmallLogo(root, titleImage);

            NetworkRunner shopRunner = Runner;
            m_ShopMode = MatchProgress.HasResults && MatchProgress.LastPassed && ItemManager.ItemsEnabled
                         && shopRunner != null && shopRunner.IsRunning;

            var column = new VisualElement();
            column.style.position = Position.Absolute;
            column.style.top = Length.Percent(26);
            column.style.left = 0;
            column.style.right = m_ShopMode ? Length.Percent(50) : 0; // the shop takes the right half
            column.style.alignItems = Align.Center;
            root.Add(column);

            if (m_ShopMode) BuildShop(root, shopRunner);

            int round = MatchProgress.HasResults ? MatchProgress.LastRoundNumber : MatchProgress.RoundNumber - 1;
            column.Add(MakeLabel(Loc.F("ROUND {0} COMPLETE!", Mathf.Max(1, round)), 40, new Color(0.55f, 0.9f, 0.43f), bold: true));
            column.Add(MakeStarLabel(Loc.F("Team scored {0} of {1}", StarLabel.Amount(MatchProgress.LastTeamTotal), StarLabel.Amount(MatchProgress.LastTeamTarget)), 20, Color.white));

            var table = new VisualElement();
            table.style.marginTop = 24;
            table.style.marginBottom = 24;
            table.style.width = 460;
            table.style.backgroundColor = new Color(1f, 1f, 1f, 0.06f);
            table.style.paddingTop = 10;
            table.style.paddingBottom = 10;
            table.style.paddingLeft = 16;
            table.style.paddingRight = 16;
            column.Add(table);

            foreach (RoundResultEntry entry in MatchProgress.LastResults)
            {
                table.Add(MakeRow(entry.Name, StarLabel.Amount(entry.Score), Color.white, new Color(0.57f, 0.84f, 0.38f)));
            }
            table.Add(MakeRow("TOTAL", StarLabel.Amount(MatchProgress.LastTeamTotal), Color.white, new Color(1f, 0.84f, 0.35f)));

            column.Add(MakeStarLabel(Loc.F("Next round's target: {0}", StarLabel.Amount(MatchProgress.NextTeamTarget)), 20, new Color(0.67f, 0.86f, 1f)));

            m_Prompt = MakeLabel(m_ShopMode ? Loc.T("Buy an item, or choose No Item, when you're ready for the next round") : "Press any key to go to the next round", 18, Color.white, bold: false);
            m_Prompt.style.position = Position.Absolute;
            m_Prompt.style.bottom = 90;
            m_Prompt.style.left = 0;
            m_Prompt.style.right = 0;
            root.Add(m_Prompt);

            m_ReadyLabel = MakeLabel("", 16, new Color(0.8f, 0.8f, 0.8f), bold: false);
            m_ReadyLabel.style.position = Position.Absolute;
            m_ReadyLabel.style.bottom = 60;
            m_ReadyLabel.style.left = 0;
            m_ReadyLabel.style.right = 0;
            root.Add(m_ReadyLabel);

            RefreshReady();
        }

        private void BuildShop(VisualElement root, NetworkRunner runner)
        {
            float standing = MatchProgress.LocalStanding(runner.LocalPlayer.PlayerId);
            int budget = Mathf.Max(0, MatchProgress.LastLocalFinalScore);
            List<ItemDefinition> offer = ItemCatalog.RollShop(standing, new System.Random());
            Debug.Log($"[ItemShop] Placing {standing:0.##} (1 = first) - offered: {string.Join(", ", offer.ConvertAll(i => $"{i.displayName} ({i.shopCost}, tier {i.shopTier})"))}. Budget {budget}.");

            m_Shop = new ItemShopPanel(budget, offer, selectSound, m_ShownTime + inputDelaySeconds);
            m_Shop.Chosen += HandleShopChoice;

            var holder = m_Shop.Root;
            holder.style.position = Position.Absolute;
            holder.style.top = Length.Percent(22);
            holder.style.left = Length.Percent(50);
            holder.style.right = 0;
            root.Add(holder);

            holder.schedule.Execute(() => m_Shop?.FocusFirst()).ExecuteLater(50);
        }

        private void HandleShopChoice(ItemDefinition item)
        {
            if (m_Ready) return;
            if (item != null)
            {
                MatchProgress.SetShopPurchase(item.id, item.shopCost);
                Debug.Log($"[ItemShop] Bought {item.displayName} for {item.shopCost} - it'll be ready when the next round starts.");
            }
            else
            {
                MatchProgress.SetShopPurchase(ItemId.None, 0);
                Debug.Log("[ItemShop] No item this time.");
            }
            BecomeReady(playSound: false);
        }

        private static Label MakeLabel(string text, int size, Color color, bool bold)
        {
            var label = new Label(text);
            label.style.fontSize = size;
            label.style.color = color;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            return label;
        }

        /// <summary>A centred line of text whose ★s are drawn as stars.</summary>
        private static StarLabel MakeStarLabel(string text, int size, Color color)
        {
            var label = new StarLabel(text, Justify.Center);
            label.style.fontSize = size;
            label.style.color = color;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            return label;
        }

        private static VisualElement MakeRow(string name, string value, Color nameColor, Color valueColor)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.paddingTop = 3;
            row.style.paddingBottom = 3;

            var nameLabel = new Label(name);
            nameLabel.style.fontSize = 18;
            nameLabel.style.color = nameColor;
            row.Add(nameLabel);

            var valueLabel = new StarLabel(value, Justify.FlexEnd);
            valueLabel.style.fontSize = 18;
            valueLabel.style.color = valueColor;
            row.Add(valueLabel);
            return row;
        }

        private IEnumerator FlashPrompt()
        {
            var wait = new WaitForSeconds(promptFlashInterval);
            bool visible = true;
            while (true)
            {
                yield return wait;
                if (m_Ready || m_Prompt == null) continue;
                visible = !visible;
                m_Prompt.style.visibility = visible ? Visibility.Visible : Visibility.Hidden;
            }
        }

        private void Update()
        {
            NetworkRunner runner = Runner;

            // With the shop open, choosing an item (or No Item) is what gets the player ready.
            if (!m_Ready && !m_ShopMode && Time.unscaledTime - m_ShownTime >= inputDelaySeconds && AnyInputPressed())
            {
                BecomeReady(playSound: true);
            }

            // The master client starts the next round once everyone in the session is ready.
            if (!m_Loading && runner != null && runner.IsRunning && runner.IsSharedModeMasterClient && EveryoneReady(runner))
            {
                m_Loading = true;
                if (!FusionSessionService.Instance.LoadSceneForEveryone(FusionSessionService.GameplaySceneName))
                {
                    m_Loading = false;
                }
            }
        }

        private void BecomeReady(bool playSound)
        {
            if (m_Ready) return;
            m_Ready = true;
            if (playSound) MenuUI.PlaySound(selectSound);
            NetworkRunner runner = Runner;
            if (runner != null && runner.IsRunning)
            {
                MatchProgress.MarkReady(runner.LocalPlayer.PlayerId);
                MatchFlowSync.ReadyRpc(runner);
            }
            m_Prompt.text = "Ready! Waiting for everyone else...";
            m_Prompt.style.visibility = Visibility.Visible;
            RefreshReady();
        }

        private static bool EveryoneReady(NetworkRunner runner)
        {
            int players = 0;
            foreach (PlayerRef player in runner.ActivePlayers)
            {
                if (!MatchProgress.IsReady(player.PlayerId)) return false;
                players++;
            }
            return players > 0;
        }

        private void RefreshReady()
        {
            if (m_ReadyLabel == null) return;
            NetworkRunner runner = Runner;
            if (runner == null || !runner.IsRunning)
            {
                m_ReadyLabel.text = "";
                return;
            }

            int players = 0, ready = 0;
            foreach (PlayerRef player in runner.ActivePlayers)
            {
                players++;
                if (MatchProgress.IsReady(player.PlayerId)) ready++;
            }
            m_ReadyLabel.text = Loc.F("{0} / {1} ready", ready, players);
        }

        private void HandleGameEnded(string message)
        {
            if (m_Exiting) return;
            m_Exiting = true;
            Debug.Log($"[RoundResults] {message} Returning to {exitSceneName}.");
            SceneManager.LoadScene(exitSceneName);
        }

        /// <summary>Any key, mouse button or gamepad button (same check as the title screen).</summary>
        public static bool AnyInputPressed()
        {
            if (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame) return true;

            if (Mouse.current != null &&
                (Mouse.current.leftButton.wasPressedThisFrame || Mouse.current.rightButton.wasPressedThisFrame))
            {
                return true;
            }

            Gamepad pad = Gamepad.current;
            return pad != null &&
                   (pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame ||
                    pad.buttonNorth.wasPressedThisFrame || pad.buttonWest.wasPressedThisFrame ||
                    pad.startButton.wasPressedThisFrame || pad.selectButton.wasPressedThisFrame);
        }
    }
}
