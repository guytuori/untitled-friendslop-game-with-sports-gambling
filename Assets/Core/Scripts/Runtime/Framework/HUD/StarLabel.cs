using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Text with little star icons in it - the game's points are "stars" (you're gambling with stars, not
    /// money), so score numbers and prices are shown with a star beside them. Write <see cref="Token"/> (★)
    /// wherever a star goes, e.g. <see cref="Amount"/>(150) = "150★"; this draws the text in between as
    /// labels and every ★ as the star picture (Resources/Pickups/Pickup.png, the pickup), sized to the font.
    /// The game font has no ★ character, so it's never drawn as text.
    ///
    /// Styling works like a Label: USS classes and inline colour / font size on this element are inherited by
    /// its pieces (and a selector like ".notification-item Label" still reaches them).
    /// </summary>
    public class StarLabel : VisualElement
    {
        /// <summary>Marks where a star goes in the text.</summary>
        public const string Token = "★";

        public const string StarTexturePath = "Pickups/Pickup";

        private const float IconScale = 1.05f;

        private static Texture2D s_Star;
        private static bool s_Tried;

        private readonly List<VisualElement> m_Icons = new List<VisualElement>();
        private string m_Text;
        private float m_IconSize = -1f;

        /// <summary>A number with a star after it ("150★").</summary>
        public static string Amount(int value) => value.ToString() + Token;

        /// <summary>The star picture (the pickup's), or null if it's missing.</summary>
        public static Texture2D StarTexture
        {
            get
            {
                if (s_Star == null && !s_Tried)
                {
                    s_Tried = true;
                    s_Star = Resources.Load<Texture2D>(StarTexturePath);
                    if (s_Star == null) Debug.LogWarning($"[StarLabel] Couldn't find Resources/{StarTexturePath}.png - stars will show as '*'.");
                }
                return s_Star;
            }
        }

        public StarLabel() : this("") { }

        public StarLabel(string text, Justify justify = Justify.FlexStart)
        {
            pickingMode = PickingMode.Ignore;
            style.flexDirection = FlexDirection.Row;
            style.flexWrap = Wrap.Wrap;
            style.alignItems = Align.Center;
            style.justifyContent = justify;
            RegisterCallback<CustomStyleResolvedEvent>(_ => SizeIcons());
            RegisterCallback<GeometryChangedEvent>(_ => SizeIcons());
            this.text = text;
        }

        public string text
        {
            get => m_Text;
            set
            {
                value ??= "";
                if (value == m_Text) return;
                m_Text = value;
                Rebuild();
            }
        }

        private void Rebuild()
        {
            Clear();
            m_Icons.Clear();
            if (string.IsNullOrEmpty(m_Text)) return;

            Texture2D star = StarTexture;
            string[] parts = m_Text.Split(Token[0]);
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0)
                {
                    var label = new Label(parts[i]) { pickingMode = PickingMode.Ignore };
                    label.style.marginLeft = 0;
                    label.style.marginRight = 0;
                    label.style.paddingLeft = 0;
                    label.style.paddingRight = 0;
                    Add(label);
                }

                if (i < parts.Length - 1)
                {
                    if (star == null)
                    {
                        var fallback = new Label("*") { pickingMode = PickingMode.Ignore };
                        Add(fallback);
                        continue;
                    }
                    var icon = new VisualElement { name = "star-icon", pickingMode = PickingMode.Ignore };
                    icon.style.backgroundImage = new StyleBackground(star);
                    icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                    icon.style.flexShrink = 0;
                    icon.style.marginLeft = 2;
                    icon.style.marginRight = 2;
                    Add(icon);
                    m_Icons.Add(icon);
                }
            }

            m_IconSize = -1f;
            SizeIcons();
        }

        private void SizeIcons()
        {
            if (m_Icons.Count == 0) return;
            float fontSize = resolvedStyle.fontSize;
            if (float.IsNaN(fontSize) || fontSize <= 0f) fontSize = 14f;
            float size = Mathf.Round(fontSize * IconScale);
            if (Mathf.Approximately(size, m_IconSize)) return; // don't re-lay out for nothing
            m_IconSize = size;
            foreach (VisualElement icon in m_Icons)
            {
                icon.style.width = size;
                icon.style.height = size;
            }
        }

        /// <summary>
        /// Sets a button's text, drawing ★ as stars: text with a star goes in a StarLabel inside the button
        /// (the button's own text is cleared), plain text stays the button's text.
        /// </summary>
        public static void SetButtonText(Button button, string value)
        {
            if (button == null) return;
            var inner = button.Q<StarLabel>("button-star-text");
            if (value == null || !value.Contains(Token))
            {
                inner?.RemoveFromHierarchy();
                if (button.text != value) button.text = value;
                return;
            }

            if (!string.IsNullOrEmpty(button.text)) button.text = "";
            if (inner == null)
            {
                inner = new StarLabel("", Justify.Center) { name = "button-star-text" };
                inner.style.flexGrow = 1;
                inner.style.flexWrap = Wrap.NoWrap;
                button.Add(inner);
            }
            inner.text = value;
        }
    }
}
