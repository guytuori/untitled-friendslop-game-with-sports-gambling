using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// An item's picture for the UI (HUD slot, slot-machine reel, between-rounds shop). Until items have
    /// icons it's a tile in the item's colour with its name written on it; once an item has an
    /// <see cref="ItemDefinition.icon"/>, the icon is shown instead.
    /// </summary>
    public static class ItemTile
    {
        /// <summary>A tile that fills its parent (position it / size the parent as needed).</summary>
        public static VisualElement Make(float fontSize)
        {
            var tile = new VisualElement { name = "item-tile", pickingMode = PickingMode.Ignore };
            tile.style.flexGrow = 1;
            tile.style.alignItems = Align.Center;
            tile.style.justifyContent = Justify.Center;
            SetRadius(tile, 6);
            tile.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);

            var label = new Label { name = "item-tile-name", pickingMode = PickingMode.Ignore };
            label.style.fontSize = fontSize;
            label.style.color = Color.white;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityTextOutlineWidth = 1.2f;
            label.style.unityTextOutlineColor = new Color(0f, 0f, 0f, 0.85f);
            label.style.paddingLeft = 4;
            label.style.paddingRight = 4;
            tile.Add(label);
            GameFont.Apply(tile);
            return tile;
        }

        /// <summary>Shows <paramref name="item"/> on a tile made by <see cref="Make"/> (null = blank).</summary>
        public static void Show(VisualElement tile, ItemDefinition item)
        {
            if (tile == null) return;
            var label = tile.Q<Label>("item-tile-name");

            if (item == null)
            {
                tile.style.backgroundColor = new Color(0f, 0f, 0f, 0f);
                tile.style.backgroundImage = StyleKeyword.None;
                SetBorder(tile, 0f, Color.clear);
                if (label != null) label.text = "";
                return;
            }

            Color c = item.color;
            if (item.icon != null)
            {
                tile.style.backgroundImage = new StyleBackground(item.icon);
                tile.style.backgroundColor = new Color(c.r, c.g, c.b, 0.35f);
                if (label != null) label.text = "";
            }
            else
            {
                tile.style.backgroundImage = StyleKeyword.None;
                tile.style.backgroundColor = new Color(c.r * 0.85f, c.g * 0.85f, c.b * 0.85f, 1f);
                if (label != null) label.text = (item.displayName ?? item.id.ToString()).ToUpperInvariant();
            }
            Color edge = Color.Lerp(c, Color.white, 0.45f);
            SetBorder(tile, 2f, edge);
        }

        public static void SetRadius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r;
            e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r;
            e.style.borderBottomRightRadius = r;
        }

        public static void SetBorder(VisualElement e, float width, Color color)
        {
            e.style.borderTopWidth = width;
            e.style.borderBottomWidth = width;
            e.style.borderLeftWidth = width;
            e.style.borderRightWidth = width;
            e.style.borderTopColor = color;
            e.style.borderBottomColor = color;
            e.style.borderLeftColor = color;
            e.style.borderRightColor = color;
        }
    }
}
