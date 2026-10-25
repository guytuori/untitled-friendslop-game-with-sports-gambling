using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The between-rounds item shop on the Round Results screen: three items (picked by
    /// <see cref="ItemCatalog.RollShop"/> from the tiers this player's placing allows), each with its price,
    /// and NO ITEM. The budget is this player's final score last round; a price above it can't be bought.
    /// Choosing one (mouse, or gamepad / keyboard navigation and submit) reports it through
    /// <see cref="Chosen"/> (null = No Item) and locks the panel.
    /// </summary>
    public class ItemShopPanel
    {
        private const float CardWidth = 190f;
        private const float CardHeight = 270f;

        private static readonly Color Gold = new Color(1f, 0.84f, 0.35f);
        private static readonly Color CardBackground = new Color(1f, 1f, 1f, 0.06f);
        private static readonly Color CardFocusedBackground = new Color(1f, 1f, 1f, 0.16f);
        private static readonly Color CardBorder = new Color(1f, 1f, 1f, 0.25f);
        private static readonly Color TooExpensive = new Color(1f, 0.35f, 0.3f);

        private readonly List<Button> m_Buttons = new List<Button>();
        private readonly Dictionary<Button, ItemDefinition> m_ItemOf = new Dictionary<Button, ItemDefinition>();
        private readonly AudioClip m_ClickSound;
        private readonly float m_AcceptAfter;
        private bool m_Done;

        public VisualElement Root { get; }

        /// <summary>The player chose: the item, or null for No Item.</summary>
        public event Action<ItemDefinition> Chosen;

        /// <param name="acceptAfter">Time.unscaledTime before which clicks are ignored (a button held from the round).</param>
        public ItemShopPanel(int budget, List<ItemDefinition> offer, AudioClip clickSound, float acceptAfter)
        {
            m_ClickSound = clickSound;
            m_AcceptAfter = acceptAfter;

            Root = new VisualElement { name = "item-shop" };
            Root.style.alignItems = Align.Center;

            var title = new Label(Loc.T("ITEM SHOP"));
            title.style.fontSize = 34;
            title.style.color = Gold;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            Root.Add(title);

            var budgetLabel = new StarLabel(Loc.F("You can spend up to {0} points (your score last round).", StarLabel.Amount(budget)), Justify.Center);
            budgetLabel.style.fontSize = 16;
            budgetLabel.style.color = Color.white;
            budgetLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            budgetLabel.style.marginTop = 4;
            Root.Add(budgetLabel);

            var note = new Label(Loc.T("The price comes off the points you start the next round with."));
            note.style.fontSize = 13;
            note.style.color = new Color(0.75f, 0.75f, 0.75f);
            note.style.unityTextAlign = TextAnchor.MiddleCenter;
            note.style.marginBottom = 16;
            Root.Add(note);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.Center;
            Root.Add(row);

            foreach (ItemDefinition item in offer)
            {
                Button card = MakeCard(item, item.shopCost <= budget);
                row.Add(card);
            }

            var noItemRow = new VisualElement();
            noItemRow.style.flexDirection = FlexDirection.Row;
            noItemRow.style.justifyContent = Justify.FlexEnd;
            noItemRow.style.width = Mathf.Max(1, offer.Count) * (CardWidth + 16f);
            noItemRow.style.marginTop = 18;
            Root.Add(noItemRow);

            Button noItem = MakeButton(Loc.T("NO ITEM"), null);
            noItem.style.width = 200;
            noItem.style.height = 46;
            noItem.style.fontSize = 20;
            noItemRow.Add(noItem);

            GameFont.Apply(Root);
        }

        /// <summary>Focuses the first item that can be bought (or No Item), for gamepad play.</summary>
        public void FocusFirst()
        {
            foreach (Button b in m_Buttons)
            {
                if (b.enabledSelf)
                {
                    b.Focus();
                    return;
                }
            }
        }

        private Button MakeCard(ItemDefinition item, bool affordable)
        {
            Button card = MakeButton(null, item);
            card.style.width = CardWidth;
            card.style.height = CardHeight;
            card.style.marginLeft = 8;
            card.style.marginRight = 8;
            card.style.flexDirection = FlexDirection.Column;
            card.style.alignItems = Align.Stretch;
            card.style.justifyContent = Justify.FlexStart;
            card.style.paddingTop = 10;
            card.style.paddingBottom = 10;
            card.style.paddingLeft = 10;
            card.style.paddingRight = 10;

            var picture = new VisualElement { pickingMode = PickingMode.Ignore };
            picture.style.height = 120;
            picture.style.flexShrink = 0;
            VisualElement tile = ItemTile.Make(20);
            ItemTile.Show(tile, item);
            picture.Add(tile);
            card.Add(picture);

            var name = new Label(item.displayName) { pickingMode = PickingMode.Ignore };
            name.style.fontSize = 17;
            name.style.color = Color.white;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.unityTextAlign = TextAnchor.MiddleCenter;
            name.style.whiteSpace = WhiteSpace.Normal;
            name.style.marginTop = 8;
            card.Add(name);

            var cost = new StarLabel(affordable ? Loc.F("{0} points", StarLabel.Amount(item.shopCost)) : Loc.F("{0} points - not enough", StarLabel.Amount(item.shopCost)), Justify.Center);
            cost.style.fontSize = 16;
            cost.style.color = affordable ? Gold : TooExpensive;
            cost.style.unityTextAlign = TextAnchor.MiddleCenter;
            cost.style.marginTop = 4;
            card.Add(cost);

            string timing = item.IsTimed ? Loc.F("Lasts {0} s", Mathf.RoundToInt(item.durationSeconds)) : Loc.T("Single use");
            var description = new Label($"{item.description}\n({timing})") { pickingMode = PickingMode.Ignore };
            description.style.fontSize = 12;
            description.style.color = new Color(0.78f, 0.78f, 0.78f);
            description.style.unityTextAlign = TextAnchor.UpperCenter;
            description.style.whiteSpace = WhiteSpace.Normal;
            description.style.marginTop = 6;
            card.Add(description);

            if (!affordable)
            {
                card.SetEnabled(false);
                card.style.opacity = 0.55f;
            }
            return card;
        }

        private Button MakeButton(string text, ItemDefinition item)
        {
            var button = new Button();
            if (!string.IsNullOrEmpty(text)) button.text = text;
            button.style.backgroundColor = CardBackground;
            button.style.color = Color.white;
            ItemTile.SetBorder(button, 3f, CardBorder);
            ItemTile.SetRadius(button, 10);
            button.style.marginTop = 0;
            button.style.marginBottom = 0;

            button.RegisterCallback<FocusInEvent>(_ => SetFocused(button, true));
            button.RegisterCallback<FocusOutEvent>(_ => SetFocused(button, false));
            button.RegisterCallback<PointerEnterEvent>(_ => { if (!m_Done && button.enabledSelf) button.Focus(); });
            button.clicked += () => Choose(button);

            m_Buttons.Add(button);
            m_ItemOf[button] = item;
            return button;
        }

        private void SetFocused(Button button, bool focused)
        {
            if (m_Done) return;
            button.style.backgroundColor = focused ? CardFocusedBackground : CardBackground;
            ItemTile.SetBorder(button, 3f, focused ? Gold : CardBorder);
            button.style.scale = new Scale(focused ? new Vector2(1.04f, 1.04f) : Vector2.one);
        }

        private void Choose(Button button)
        {
            if (m_Done || Time.unscaledTime < m_AcceptAfter) return;
            m_Done = true;
            MenuUI.PlaySound(m_ClickSound);

            ItemDefinition item = m_ItemOf.TryGetValue(button, out ItemDefinition chosen) ? chosen : null;
            foreach (Button b in m_Buttons)
            {
                bool picked = b == button;
                b.SetEnabled(false);
                b.style.opacity = picked ? 1f : 0.35f;
                b.style.scale = new Scale(picked ? new Vector2(1.06f, 1.06f) : Vector2.one);
                ItemTile.SetBorder(b, 3f, picked ? new Color(0.45f, 0.9f, 0.4f) : CardBorder);
            }
            Chosen?.Invoke(item);
        }
    }
}
