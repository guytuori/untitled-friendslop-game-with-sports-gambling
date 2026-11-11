using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// This player's view of the wager ticker (WagerManager), drawn on their HUD (CoreHUD adds this at
    /// runtime - no scene setup):
    ///
    /// TICKER (top middle, under the timers): the wager being offered right now, with YES / NO prices and
    /// DISMISS, and the next three waiting below it, dimmer. Answered with the Wager Yes / Wager No / Dismiss
    /// Wager keys from Change Keybindings (1 / 2 / 3, or RB / RT / LT by default), or by clicking while the
    /// cursor is free. Only wagers this player can bet on are listed: still open for betting, not about
    /// themselves (unless they're alone), not already answered, and not after they've finished the round.
    ///
    /// PHONE (bottom right): this player's bets that haven't settled yet - the wager and what they bet. If
    /// they don't all fit, the list scrolls up slowly in a loop, about once every 10 seconds. A settled bet
    /// disappears and a notification says whether it won.
    ///
    /// OTHER PLAYERS' PREDICTIONS (2026-11-10): View Previous / Next Player's Predictions (5 / T, D-Pad Left /
    /// Right by default) flip the phone to another player's open predictions, in scoreboard order and round
    /// again - so nobody has to keep asking "what do you need me to do?". The phone's top line is the name of
    /// the player it's showing, and two key badges with arrows on its top corners show the keys. It starts on
    /// this player's own each round. Everyone's accepted bets reach every client (WagerManager.BetAccepted).
    /// </summary>
    public class WagerHUD : MonoBehaviour
    {
        private const int TickerRows = 4; // the current wager + the next three
        private const float TickerWidth = 920f;
        private const float PhoneWidth = 220f;
        private const float PhoneAspect = 1751f / 792f; // phone.png
        private const float ScrollLoopSeconds = 10f;
        private const float MinScrollSpeed = 12f;
        private const float ScrollGap = 24f;
        private const string PhoneTexturePath = "Wagers/Phone";

        private static readonly Color YesColor = new Color(0.1f, 0.55f, 0.15f);
        private static readonly Color NoColor = new Color(0.85f, 0.1f, 0.1f);
        private static readonly Color DismissColor = new Color(0.45f, 0.45f, 0.45f);

        private class TickerRow
        {
            public VisualElement Root;
            public Label Text;
            public Button Yes;
            public Button No;
            public Button Dismiss;
            public int WagerId = -1;
        }

        private class PhoneEntry
        {
            public int WagerId;
            public string Text;
            public bool Yes;
            public int Cost;
        }

        private Action<string> m_Notify;
        private WagerManager m_Manager;
        private VisualElement m_Ticker;
        private readonly List<TickerRow> m_Rows = new List<TickerRow>();
        private readonly HashSet<int> m_Answered = new HashSet<int>();
        private readonly Dictionary<int, string> m_AnsweredText = new Dictionary<int, string>();
        private readonly List<PhoneEntry> m_PhoneEntries = new List<PhoneEntry>();
        private List<WagerManager.WagerView> m_Offers = new List<WagerManager.WagerView>();

        // Whose predictions the phone shows (player id) and everyone's open ones (player id -> entries).
        private readonly Dictionary<int, List<PhoneEntry>> m_PhoneByPlayer = new Dictionary<int, List<PhoneEntry>>();
        private int m_ViewedPlayer = -1;
        private Label m_PhoneHeader;
        private VisualElement m_PrevBadge, m_NextBadge;
        private Label m_PrevKey, m_NextKey;

        private VisualElement m_PhoneViewport;
        private VisualElement m_PhoneScroller;
        private VisualElement m_PhoneCopyA;
        private VisualElement m_PhoneCopyB;
        private float m_ScrollOffset;
        private float m_NextRefreshTime;

        private InputBindingsData m_Bindings;

        public void Initialize(VisualElement root, Action<string> notify)
        {
            m_Notify = notify;
            m_Bindings = InputBindingsStore.Load();
            InputBindingsStore.Saved += ReloadBindings; // rebound mid-game (Esc > Settings > Change Keybindings)
            BuildTicker(root);
            BuildPhone(root);
            RefreshPhone();
        }

        private void ReloadBindings() => m_Bindings = InputBindingsStore.Load();

        private void OnDestroy()
        {
            InputBindingsStore.Saved -= ReloadBindings;
            Unsubscribe();
            m_Ticker?.RemoveFromHierarchy();
            m_PhoneViewport?.parent?.RemoveFromHierarchy();
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        private void BuildTicker(VisualElement root)
        {
            m_Ticker = new VisualElement { name = "wager-ticker", pickingMode = PickingMode.Ignore };
            m_Ticker.style.position = Position.Absolute;
            m_Ticker.style.top = 104; // under the round/bonus timers
            m_Ticker.style.left = Length.Percent(50);
            m_Ticker.style.width = TickerWidth;
            m_Ticker.style.maxWidth = Length.Percent(94);
            m_Ticker.style.translate = new Translate(Length.Percent(-50), 0);
            m_Ticker.style.flexDirection = FlexDirection.Column;
            root.Add(m_Ticker);

            for (int i = 0; i < TickerRows; i++)
            {
                bool current = i == 0;
                var row = new TickerRow();
                row.Root = new VisualElement();
                row.Root.style.flexDirection = FlexDirection.Row;
                row.Root.style.alignItems = Align.Center;
                row.Root.style.height = current ? 46 : 34;
                row.Root.style.paddingLeft = 12;
                row.Root.style.paddingRight = 8;
                row.Root.style.marginBottom = 3;
                row.Root.style.backgroundColor = new Color(1f, 1f, 1f, current ? 0.95f : 0.85f);
                row.Root.style.borderTopWidth = row.Root.style.borderBottomWidth = row.Root.style.borderLeftWidth = row.Root.style.borderRightWidth = current ? 3 : 1;
                Color border = current ? new Color(1f, 0.8f, 0.2f) : new Color(0.1f, 0.1f, 0.1f);
                row.Root.style.borderTopColor = row.Root.style.borderBottomColor = row.Root.style.borderLeftColor = row.Root.style.borderRightColor = border;
                row.Root.style.opacity = current ? 1f : 0.85f - 0.15f * i;

                row.Text = new Label("");
                row.Text.style.flexGrow = 1;
                row.Text.style.flexShrink = 1;
                row.Text.style.color = Color.black;
                row.Text.style.fontSize = current ? 16 : 13;
                row.Text.style.whiteSpace = WhiteSpace.NoWrap;
                row.Text.style.overflow = Overflow.Hidden;
                row.Text.style.textOverflow = TextOverflow.Ellipsis;
                row.Root.Add(row.Text);

                int index = i;
                row.Yes = MakeTickerButton(YesColor, current, () => Answer(index, AnswerKind.Yes));
                row.No = MakeTickerButton(NoColor, current, () => Answer(index, AnswerKind.No));
                row.Dismiss = MakeTickerButton(DismissColor, current, () => Answer(index, AnswerKind.Dismiss));
                row.Root.Add(row.Yes);
                row.Root.Add(row.No);
                row.Root.Add(row.Dismiss);

                row.Root.style.display = DisplayStyle.None;
                m_Ticker.Add(row.Root);
                m_Rows.Add(row);
            }
        }

        private static Button MakeTickerButton(Color color, bool current, Action onClick)
        {
            var button = new Button(onClick) { focusable = false };
            MenuUI.ApplyButtonFont(button);
            button.style.backgroundColor = color;
            button.style.color = Color.white;
            button.style.fontSize = current ? 15 : 12;
            button.style.width = current ? 132 : 92;
            button.style.height = current ? 34 : 26;
            button.style.marginLeft = 6;
            button.style.borderTopWidth = button.style.borderBottomWidth = button.style.borderLeftWidth = button.style.borderRightWidth = 0;
            return button;
        }

        private void BuildPhone(VisualElement root)
        {
            float height = PhoneWidth * PhoneAspect;

            var phone = new VisualElement { name = "wager-phone", pickingMode = PickingMode.Ignore };
            phone.style.position = Position.Absolute;
            phone.style.right = 16;
            phone.style.bottom = 16;
            phone.style.width = PhoneWidth;
            phone.style.height = height;
            root.Add(phone);

            // Screen first (behind), then the phone frame on top - the frame's screen area is transparent.
            m_PhoneViewport = new VisualElement { pickingMode = PickingMode.Ignore };
            m_PhoneViewport.style.position = Position.Absolute;
            m_PhoneViewport.style.left = Length.Percent(4.3f);
            m_PhoneViewport.style.right = Length.Percent(4.3f);
            m_PhoneViewport.style.top = Length.Percent(1.8f);
            m_PhoneViewport.style.bottom = Length.Percent(1.8f);
            m_PhoneViewport.style.backgroundColor = new Color(0.06f, 0.07f, 0.11f, 0.92f);
            m_PhoneViewport.style.borderTopLeftRadius = m_PhoneViewport.style.borderTopRightRadius = 18;
            m_PhoneViewport.style.borderBottomLeftRadius = m_PhoneViewport.style.borderBottomRightRadius = 18;
            m_PhoneViewport.style.overflow = Overflow.Hidden;
            phone.Add(m_PhoneViewport);

            var header = new Label("");
            m_PhoneHeader = header;
            header.style.position = Position.Absolute;
            header.style.top = Length.Percent(4.5f); // below the camera hole
            header.style.left = 0;
            header.style.right = 0;
            header.style.unityTextAlign = TextAnchor.MiddleCenter;
            header.style.color = new Color(1f, 0.8f, 0.3f);
            header.style.fontSize = 11;
            m_PhoneViewport.Add(header);

            // The scrolling list lives in its own clipped area under the header.
            var listArea = new VisualElement { pickingMode = PickingMode.Ignore };
            listArea.style.position = Position.Absolute;
            listArea.style.top = Length.Percent(8.5f);
            listArea.style.bottom = 10;
            listArea.style.left = 8;
            listArea.style.right = 8;
            listArea.style.overflow = Overflow.Hidden;
            m_PhoneViewport.Add(listArea);

            m_PhoneScroller = new VisualElement { pickingMode = PickingMode.Ignore };
            m_PhoneScroller.style.position = Position.Absolute;
            m_PhoneScroller.style.left = 0;
            m_PhoneScroller.style.right = 0;
            m_PhoneScroller.style.top = 0;
            listArea.Add(m_PhoneScroller);

            m_PhoneCopyA = new VisualElement { pickingMode = PickingMode.Ignore };
            m_PhoneCopyB = new VisualElement { pickingMode = PickingMode.Ignore };
            m_PhoneCopyB.style.marginTop = ScrollGap;
            m_PhoneScroller.Add(m_PhoneCopyA);
            m_PhoneScroller.Add(m_PhoneCopyB);

            // Phone frame on top.
            var texture = Resources.Load<Texture2D>(PhoneTexturePath);
            if (texture != null)
            {
                var frame = new Image { image = texture, scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
                frame.style.position = Position.Absolute;
                frame.style.left = frame.style.right = frame.style.top = frame.style.bottom = 0;
                phone.Add(frame);
            }
            else
            {
                Debug.LogWarning($"[WagerHUD] Couldn't load Resources/{PhoneTexturePath} - the phone has no frame.");
            }

            // The keys that flip to another player's predictions, on the phone's top corners.
            m_PrevBadge = MakeViewBadge(phone, left: true, out m_PrevKey);
            m_NextBadge = MakeViewBadge(phone, left: false, out m_NextKey);

            // The list area is measured against the viewport, so scrolling needs it laid out.
            listArea.RegisterCallback<GeometryChangedEvent>(_ => m_ListAreaHeight = listArea.resolvedStyle.height);
        }

        private float m_ListAreaHeight;

        // =====================================================================================
        // Wiring
        // =====================================================================================

        private void Subscribe()
        {
            WagerManager manager = WagerManager.Instance;
            if (manager == m_Manager) return;
            Unsubscribe();
            m_Manager = manager;
            if (m_Manager == null) return;

            m_Manager.WagersChanged += RefreshTicker;
            m_Manager.BetAccepted += HandleBetAccepted;
            m_Manager.LocalBetRejected += HandleBetRejected;
            m_Manager.WagerSettled += HandleWagerSettled;
        }

        private void Unsubscribe()
        {
            if (m_Manager == null) return;
            m_Manager.WagersChanged -= RefreshTicker;
            m_Manager.BetAccepted -= HandleBetAccepted;
            m_Manager.LocalBetRejected -= HandleBetRejected;
            m_Manager.WagerSettled -= HandleWagerSettled;
            m_Manager = null;
        }

        private void Update()
        {
            Subscribe();

            if (Time.unscaledTime >= m_NextRefreshTime)
            {
                m_NextRefreshTime = Time.unscaledTime + 0.2f; // prices move as players play
                RefreshTicker();
            }

            if (!InGameMenu.IsOpen && !ChatOverlay.CapturesInput)
            {
                if (BoundControls.WasPressed(m_Bindings.Keyboard.ViewPreviousPredictions, m_Bindings.Gamepad.ViewPreviousPredictions)) CycleViewedPlayer(-1);
                else if (BoundControls.WasPressed(m_Bindings.Keyboard.ViewNextPredictions, m_Bindings.Gamepad.ViewNextPredictions)) CycleViewedPlayer(1);
            }
            UpdatePhoneHeader();

            if (!InGameMenu.IsOpen && !ChatOverlay.CapturesInput && m_Offers.Count > 0)
            {
                if (Pressed(m_Bindings.Keyboard.BetYes, m_Bindings.Gamepad.BetYes)) Answer(0, AnswerKind.Yes);
                else if (Pressed(m_Bindings.Keyboard.BetNo, m_Bindings.Gamepad.BetNo)) Answer(0, AnswerKind.No);
                else if (Pressed(m_Bindings.Keyboard.DismissBet, m_Bindings.Gamepad.DismissBet)) Answer(0, AnswerKind.Dismiss);
            }

            ScrollPhone(Time.unscaledDeltaTime);
        }

        // =====================================================================================
        // Ticker
        // =====================================================================================

        private enum AnswerKind { Yes, No, Dismiss }

        private void RefreshTicker()
        {
            m_Offers = new List<WagerManager.WagerView>();
            ulong me = NetworkPlayers.LocalClientId;

            if (m_Manager != null && m_Manager.IsSpawned && ChallengeManager.IsRoundOpenFor(me))
            {
                foreach (WagerManager.WagerView view in m_Manager.GetOpenWagers())
                {
                    if (!view.BettingOpen || m_Answered.Contains(view.Id)) continue;
                    if (!m_Manager.CanBetOnSubject(me, view.SubjectId)) continue;
                    m_Offers.Add(view);
                }
            }

            bool gamepadHints = UsingGamepad();
            for (int i = 0; i < m_Rows.Count; i++)
            {
                TickerRow row = m_Rows[i];
                if (i >= m_Offers.Count)
                {
                    row.Root.style.display = DisplayStyle.None;
                    row.WagerId = -1;
                    continue;
                }

                WagerManager.WagerView view = m_Offers[i];
                row.Root.style.display = DisplayStyle.Flex;
                row.WagerId = view.Id;
                row.Text.text = view.Text;

                string yesKey = i == 0 ? KeyHint(m_Bindings.Keyboard.BetYes, m_Bindings.Gamepad.BetYes, gamepadHints) : "";
                string noKey = i == 0 ? KeyHint(m_Bindings.Keyboard.BetNo, m_Bindings.Gamepad.BetNo, gamepadHints) : "";
                string dismissKey = i == 0 ? KeyHint(m_Bindings.Keyboard.DismissBet, m_Bindings.Gamepad.DismissBet, gamepadHints) : "";
                StarLabel.SetButtonText(row.Yes, Loc.F("YES {0}", StarLabel.Amount(view.YesCost)) + yesKey);
                StarLabel.SetButtonText(row.No, Loc.F("NO {0}", StarLabel.Amount(view.NoCost)) + noKey);
                row.Dismiss.text = Loc.T("DISMISS") + dismissKey;
            }
        }

        private void Answer(int rowIndex, AnswerKind kind)
        {
            if (rowIndex < 0 || rowIndex >= m_Offers.Count || m_Manager == null) return;
            WagerManager.WagerView view = m_Offers[rowIndex];

            m_Answered.Add(view.Id);
            if (kind != AnswerKind.Dismiss)
            {
                m_AnsweredText[view.Id] = view.Text;
                m_Manager.PlaceBet(view.Id, kind == AnswerKind.Yes);
            }
            RefreshTicker();
        }

        private void HandleBetAccepted(int playerId, int wagerId, bool yes, int cost)
        {
            string text = null;
            bool mine = (ulong)playerId == NetworkPlayers.LocalClientId;
            if (mine) m_AnsweredText.TryGetValue(wagerId, out text);
            if (text == null && m_Manager != null) m_Manager.TryGetWagerText(wagerId, out text);

            var entry = new PhoneEntry { WagerId = wagerId, Text = text ?? Loc.T("Prediction"), Yes = yes, Cost = cost };
            if (mine) m_PhoneEntries.Add(entry);
            EntriesOf(playerId).Add(entry);
            if (playerId == ViewedPlayer) RefreshPhone();
        }

        private List<PhoneEntry> EntriesOf(int playerId)
        {
            if (!m_PhoneByPlayer.TryGetValue(playerId, out List<PhoneEntry> list))
            {
                list = new List<PhoneEntry>();
                m_PhoneByPlayer[playerId] = list;
            }
            return list;
        }

        /// <summary>The player whose predictions the phone shows (this player unless flipped).</summary>
        private int ViewedPlayer => m_ViewedPlayer >= 0 ? m_ViewedPlayer : (int)NetworkPlayers.LocalClientId;

        /// <summary>The players to flip through: scoreboard order (join order without a HUD).</summary>
        private static List<ulong> PlayerOrder()
        {
            var order = new List<ulong>();
            if (CoreHUD.Local != null && CoreHUD.Local.ScoreboardOrder.Count > 0) order.AddRange(CoreHUD.Local.ScoreboardOrder);
            else order.AddRange(NetworkPlayers.Ids);
            return order;
        }

        private void CycleViewedPlayer(int step)
        {
            List<ulong> order = PlayerOrder();
            if (order.Count < 2) return;
            int index = order.IndexOf((ulong)ViewedPlayer);
            if (index < 0) index = 0;
            index = ((index + step) % order.Count + order.Count) % order.Count;
            m_ViewedPlayer = (int)order[index];
            RefreshPhone();
        }

        private void UpdatePhoneHeader()
        {
            if (m_PhoneHeader == null) return;

            // Someone left: back to this player's own.
            List<ulong> order = PlayerOrder();
            if (m_ViewedPlayer >= 0 && !order.Contains((ulong)m_ViewedPlayer))
            {
                m_ViewedPlayer = -1;
                RefreshPhone();
            }

            string name = NetworkPlayers.GetDisplayName((ulong)ViewedPlayer);
            if (m_PhoneHeader.text != name) m_PhoneHeader.text = name;
            m_PhoneHeader.style.color = ViewedPlayer == (int)NetworkPlayers.LocalClientId ? new Color(1f, 0.8f, 0.3f) : new Color(0.55f, 0.85f, 1f);

            bool show = order.Count > 1;
            DisplayStyle display = show ? DisplayStyle.Flex : DisplayStyle.None;
            m_PrevBadge.style.display = display;
            m_NextBadge.style.display = display;
            if (!show || m_Bindings == null) return;

            bool gamepad = UsingGamepad();
            SetBadgeKey(m_PrevKey, gamepad ? m_Bindings.Gamepad.ViewPreviousPredictions : m_Bindings.Keyboard.ViewPreviousPredictions, gamepad);
            SetBadgeKey(m_NextKey, gamepad ? m_Bindings.Gamepad.ViewNextPredictions : m_Bindings.Keyboard.ViewNextPredictions, gamepad);
        }

        private static void SetBadgeKey(Label label, string control, bool gamepad)
        {
            // The arrow beside the badge gives the direction, so a d-pad button just says D-Pad.
            string text = gamepad
                ? (control != null && control.StartsWith("dpad/") ? Loc.T("D-Pad") : ControlNames.Gamepad(control))
                : ControlNames.Keyboard(control);
            text ??= "";
            if (label.text != text) label.text = text;
            label.style.fontSize = text.Length <= 2 ? 16 : text.Length <= 5 ? 11 : 9;
        }

        /// <summary>A white key badge (like the item slot's) hanging off a top corner of the phone, with an arrow.</summary>
        private static VisualElement MakeViewBadge(VisualElement phone, bool left, out Label key)
        {
            var holder = new VisualElement { pickingMode = PickingMode.Ignore };
            holder.style.position = Position.Absolute;
            holder.style.top = -12;
            if (left) holder.style.left = -12; else holder.style.right = -12;
            holder.style.flexDirection = left ? FlexDirection.Row : FlexDirection.RowReverse;
            holder.style.alignItems = Align.Center;

            var badge = new VisualElement { pickingMode = PickingMode.Ignore };
            badge.style.minWidth = 34;
            badge.style.height = 34;
            badge.style.paddingLeft = 4;
            badge.style.paddingRight = 4;
            badge.style.alignItems = Align.Center;
            badge.style.justifyContent = Justify.Center;
            badge.style.backgroundColor = Color.white;
            ItemTile.SetRadius(badge, 17);
            ItemTile.SetBorder(badge, 2.5f, Color.black);
            key = new Label { pickingMode = PickingMode.Ignore };
            key.style.color = Color.black;
            key.style.unityFontStyleAndWeight = FontStyle.Bold;
            key.style.unityTextAlign = TextAnchor.MiddleCenter;
            badge.Add(key);
            holder.Add(badge);

            var arrow = new Label(left ? "<" : ">") { pickingMode = PickingMode.Ignore };
            arrow.style.fontSize = 22;
            arrow.style.color = Color.white;
            arrow.style.unityFontStyleAndWeight = FontStyle.Bold;
            arrow.style.unityTextOutlineWidth = 2f;
            arrow.style.unityTextOutlineColor = Color.black;
            arrow.style.marginLeft = 3;
            arrow.style.marginRight = 3;
            holder.Add(arrow);

            phone.Add(holder);
            return holder;
        }

        private void HandleBetRejected(int wagerId, string reason)
        {
            m_Answered.Remove(wagerId); // back on the ticker, if it's still open
            m_Notify?.Invoke(reason);
            RefreshTicker();
        }

        private void HandleWagerSettled(int wagerId, bool yesWon, bool refunded)
        {
            m_Answered.Remove(wagerId);
            m_AnsweredText.Remove(wagerId);

            foreach (List<PhoneEntry> list in m_PhoneByPlayer.Values) list.RemoveAll(e => e.WagerId == wagerId);
            RefreshPhone();

            PhoneEntry entry = m_PhoneEntries.Find(e => e.WagerId == wagerId);
            if (entry == null) return;
            m_PhoneEntries.Remove(entry);

            if (refunded)
            {
                m_Notify?.Invoke(Loc.F("Refunded {0}: {1}", StarLabel.Amount(entry.Cost), entry.Text));
            }
            else if (entry.Yes == yesWon)
            {
                float multiplier = m_Manager != null ? m_Manager.PayoutMultiplier : 3f;
                m_Notify?.Invoke(Loc.F("WON +{0}: {1}", StarLabel.Amount(Mathf.RoundToInt(entry.Cost * multiplier)), entry.Text));
            }
            else
            {
                m_Notify?.Invoke(Loc.F("Lost {0}: {1}", StarLabel.Amount(entry.Cost), entry.Text));
            }
        }

        // =====================================================================================
        // Phone
        // =====================================================================================

        private void RefreshPhone()
        {
            if (m_PhoneCopyA == null) return;
            FillPhoneList(m_PhoneCopyA);
            FillPhoneList(m_PhoneCopyB);
            m_ScrollOffset = 0f;
        }

        private void FillPhoneList(VisualElement list)
        {
            list.Clear();
            List<PhoneEntry> entries = EntriesOf(ViewedPlayer);
            if (entries.Count == 0)
            {
                var empty = new Label("No active predictions");
                empty.style.color = new Color(0.6f, 0.6f, 0.65f);
                empty.style.fontSize = 11;
                empty.style.unityTextAlign = TextAnchor.MiddleCenter;
                empty.style.marginTop = 8;
                list.Add(empty);
                return;
            }

            foreach (PhoneEntry entry in entries)
            {
                var item = new VisualElement();
                item.style.marginBottom = 8;
                item.style.paddingBottom = 6;
                item.style.borderBottomWidth = 1;
                item.style.borderBottomColor = new Color(1f, 1f, 1f, 0.12f);

                var text = new Label(entry.Text);
                text.style.color = Color.white;
                text.style.fontSize = 10;
                text.style.whiteSpace = WhiteSpace.Normal;
                item.Add(text);

                var chip = new StarLabel(Loc.F(entry.Yes ? "YES {0}" : "NO {0}", StarLabel.Amount(entry.Cost)));
                chip.style.alignSelf = Align.FlexStart;
                chip.style.marginTop = 3;
                chip.style.paddingLeft = 6;
                chip.style.paddingRight = 6;
                chip.style.paddingTop = 1;
                chip.style.paddingBottom = 1;
                chip.style.fontSize = 10;
                chip.style.color = Color.white;
                chip.style.backgroundColor = entry.Yes ? YesColor : NoColor;
                item.Add(chip);

                list.Add(item);
            }
        }

        /// <summary>When the bets don't fit, scrolls the list up in an endless loop (two copies, back to back).</summary>
        private void ScrollPhone(float deltaTime)
        {
            if (m_PhoneScroller == null) return;

            float contentHeight = m_PhoneCopyA.resolvedStyle.height;
            bool overflowing = !float.IsNaN(contentHeight) && m_ListAreaHeight > 0f && contentHeight > m_ListAreaHeight;

            m_PhoneCopyB.style.display = overflowing ? DisplayStyle.Flex : DisplayStyle.None;
            if (!overflowing)
            {
                m_ScrollOffset = 0f;
                m_PhoneScroller.style.top = 0;
                return;
            }

            float loop = contentHeight + ScrollGap;
            float speed = Mathf.Max(MinScrollSpeed, loop / ScrollLoopSeconds);
            m_ScrollOffset = (m_ScrollOffset + speed * deltaTime) % loop;
            m_PhoneScroller.style.top = -m_ScrollOffset;
        }

        // =====================================================================================
        // Keys
        // =====================================================================================

        /// <summary>The saved Wager key (keyboard) or button (gamepad) was pressed this frame.</summary>
        private static bool Pressed(string keyboardControl, string gamepadControl)
        {
            if (Keyboard.current != null && !string.IsNullOrEmpty(keyboardControl))
            {
                var key = Keyboard.current.TryGetChildControl<KeyControl>(keyboardControl);
                if (key != null && key.wasPressedThisFrame) return true;
            }

            Gamepad pad = Gamepad.current;
            if (pad != null && !string.IsNullOrEmpty(gamepadControl) && Application.isFocused)
            {
                ButtonControl button = FindButton(pad, gamepadControl);
                if (button != null && button.wasPressedThisFrame) return true;
            }
            return false;
        }

        private static ButtonControl FindButton(Gamepad pad, string name)
        {
            var direct = pad.TryGetChildControl<ButtonControl>(name);
            if (direct != null) return direct;
            foreach (InputControl control in pad.allControls)
            {
                if (control is ButtonControl button && control.name == name) return button;
            }
            return null;
        }

        private static bool UsingGamepad()
        {
            Gamepad pad = Gamepad.current;
            if (pad == null) return false;
            Keyboard keyboard = Keyboard.current;
            return keyboard == null || pad.lastUpdateTime > keyboard.lastUpdateTime;
        }

        private static string KeyHint(string keyboardControl, string gamepadControl, bool gamepad)
        {
            // Same labels as the Change Keybindings screen (L1/R1/L2/R2, Nintendo-style face buttons).
            string name = gamepad ? ControlNames.Gamepad(gamepadControl) : ControlNames.Keyboard(keyboardControl);
            return string.IsNullOrEmpty(name) ? "" : $"  [{name}]";
        }
    }
}
