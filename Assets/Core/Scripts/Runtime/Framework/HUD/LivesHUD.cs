using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// "LIVES: 3" in the bottom-left corner of the HUD (CoreHUD adds it at runtime), from this player's
    /// <see cref="PlayerScore"/>: the number of lives left, or an infinity sign with unlimited lives.
    ///
    ///   - Gaining a life (1-Up): the number pops up big and flashes green.
    ///   - Losing one: it flashes red and shakes; at 0 it stays red.
    ///   - The 1-Up's death shield (unlimited lives: the next death costs no points): a see-through gold glow
    ///     around LIVES, gently pulsing, that bursts and fades when a death uses it up.
    /// </summary>
    public class LivesHUD : MonoBehaviour
    {
        private const float FontSize = 30f;
        private const float PopSeconds = 0.45f;
        private const float FlashSeconds = 0.6f;
        private const float ShieldInSeconds = 0.35f;
        private const float ShieldBreakSeconds = 0.45f;

        private static readonly Color GainColor = new Color(0.45f, 1f, 0.45f);
        private static readonly Color LoseColor = new Color(1f, 0.3f, 0.25f);
        private static readonly Color ShieldColor = new Color(1f, 0.82f, 0.2f);

        private VisualElement m_Root;
        private VisualElement m_Shield;
        private VisualElement[] m_ShieldLayers;
        private Label m_Caption;
        private Label m_Value;

        private PlayerScore m_Score;
        private int m_ShownLives = int.MinValue;
        private bool m_ShownShield;
        private bool m_Initialized;
        private float m_PopStart = -10f;
        private float m_FlashStart = -10f;
        private Color m_FlashColor = Color.white;
        private float m_ShakeStart = -10f;
        private float m_ShieldInStart = -10f;
        private float m_ShieldBreakStart = -10f;

        public void Initialize(VisualElement parent)
        {
            m_Root = new VisualElement { name = "lives", pickingMode = PickingMode.Ignore };
            m_Root.style.position = Position.Absolute;
            m_Root.style.left = 22;
            m_Root.style.bottom = 20;
            m_Root.style.flexDirection = FlexDirection.Row;
            m_Root.style.alignItems = Align.Center;
            m_Root.style.paddingLeft = 14;
            m_Root.style.paddingRight = 14;
            m_Root.style.paddingTop = 4;
            m_Root.style.paddingBottom = 4;
            parent.Add(m_Root);

            // The shield glow: a few see-through gold pills, each a little bigger and fainter (UI Toolkit has no blur).
            m_Shield = new VisualElement { pickingMode = PickingMode.Ignore };
            m_Shield.style.position = Position.Absolute;
            m_Shield.style.left = m_Shield.style.right = m_Shield.style.top = m_Shield.style.bottom = 0;
            m_Shield.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(50));
            m_Shield.style.display = DisplayStyle.None;
            m_Root.Add(m_Shield);

            float[] grow = { 10f, 5f, 0f };
            float[] alpha = { 0.12f, 0.2f, 0.42f };
            m_ShieldLayers = new VisualElement[grow.Length];
            for (int i = 0; i < grow.Length; i++)
            {
                var layer = new VisualElement { pickingMode = PickingMode.Ignore };
                layer.style.position = Position.Absolute;
                layer.style.left = layer.style.right = layer.style.top = layer.style.bottom = -grow[i];
                layer.style.backgroundColor = new Color(ShieldColor.r, ShieldColor.g, ShieldColor.b, alpha[i]);
                ItemTile.SetRadius(layer, 40f);
                if (i == grow.Length - 1) ItemTile.SetBorder(layer, 2f, new Color(1f, 0.92f, 0.5f, 0.9f));
                m_ShieldLayers[i] = layer;
                m_Shield.Add(layer);
            }

            m_Caption = MakeLabel(Loc.T("LIVES:"));
            m_Caption.style.marginRight = 8;
            m_Root.Add(m_Caption);

            m_Value = MakeLabel("");
            m_Value.style.minWidth = 30;
            m_Value.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(50));
            m_Root.Add(m_Value);

            GameFont.Apply(m_Root);
            m_Root.style.display = DisplayStyle.None; // until the player has spawned
        }

        private static Label MakeLabel(string text)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.style.fontSize = FontSize;
            label.style.color = Color.white;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.unityTextOutlineWidth = 2f;
            label.style.unityTextOutlineColor = Color.black;
            label.style.marginLeft = 0;
            label.style.marginRight = 0;
            label.style.paddingLeft = 0;
            label.style.paddingRight = 0;
            return label;
        }

        private void OnDestroy()
        {
            m_Root?.RemoveFromHierarchy();
        }

        private void Update()
        {
            if (m_Root == null) return;

            if (m_Score == null)
            {
                CorePlayerManager local = NetworkPlayers.Local;
                if (local != null) m_Score = local.GetComponent<PlayerScore>();
            }
            if (m_Score == null || !m_Score.IsSpawned)
            {
                m_Root.style.display = DisplayStyle.None;
                return;
            }
            m_Root.style.display = DisplayStyle.Flex;

            float now = Time.time;
            int lives = m_Score.Lives;
            bool shield = m_Score.HasDeathShield;

            if (!m_Initialized)
            {
                m_Initialized = true;
                m_ShownLives = lives;
                m_ShownShield = shield;
                m_Shield.style.display = shield ? DisplayStyle.Flex : DisplayStyle.None;
            }
            else
            {
                if (lives != m_ShownLives)
                {
                    if (lives > m_ShownLives) { m_PopStart = now; Flash(GainColor, now); }
                    else { m_ShakeStart = now; Flash(LoseColor, now); }
                    m_ShownLives = lives;
                }
                if (shield != m_ShownShield)
                {
                    if (shield) { m_ShieldInStart = now; m_PopStart = now; Flash(ShieldColor, now); }
                    else m_ShieldBreakStart = now;
                    m_ShownShield = shield;
                }
            }

            m_Value.text = lives < 0 ? "∞" : lives.ToString();
            AnimateValue(now, lives);
            AnimateShield(now, shield);
        }

        private void Flash(Color color, float now)
        {
            m_FlashColor = color;
            m_FlashStart = now;
        }

        private void AnimateValue(float now, int lives)
        {
            Color baseColor = lives == 0 ? LoseColor : Color.white;
            float flash = (now - m_FlashStart) / FlashSeconds;
            Color color = flash >= 0f && flash < 1f ? Color.Lerp(m_FlashColor, baseColor, flash) : baseColor;
            m_Value.style.color = color;
            m_Caption.style.color = flash >= 0f && flash < 1f ? Color.Lerp(m_FlashColor, Color.white, flash) : Color.white;

            float scale = 1f;
            float pop = (now - m_PopStart) / PopSeconds;
            if (pop >= 0f && pop < 1f) scale = 1f + 0.7f * Mathf.Exp(-5f * pop) * Mathf.Cos(pop * Mathf.PI * 2.5f);
            m_Value.style.scale = new Scale(new Vector2(scale, scale));

            float shake = (now - m_ShakeStart) / 0.4f;
            float dx = shake >= 0f && shake < 1f ? Mathf.Sin(shake * Mathf.PI * 10f) * 6f * (1f - shake) : 0f;
            m_Root.style.translate = new Translate(dx, 0);
        }

        private void AnimateShield(float now, bool shield)
        {
            float breakT = (now - m_ShieldBreakStart) / ShieldBreakSeconds;
            bool breaking = !shield && breakT >= 0f && breakT < 1f;

            if (!shield && !breaking)
            {
                m_Shield.style.display = DisplayStyle.None;
                return;
            }
            m_Shield.style.display = DisplayStyle.Flex;

            if (breaking)
            {
                // Burst outward and fade away.
                float s = 1f + 0.6f * breakT;
                m_Shield.style.scale = new Scale(new Vector2(s, s));
                m_Shield.style.opacity = 1f - breakT;
                return;
            }

            float inT = (now - m_ShieldInStart) / ShieldInSeconds;
            float scale, opacity;
            if (inT >= 0f && inT < 1f)
            {
                scale = Mathf.Lerp(1.4f, 1f, inT);
                opacity = inT;
            }
            else
            {
                // A slow breathing glow.
                float breathe = 0.5f + 0.5f * Mathf.Sin(now * 3f);
                scale = 1f + 0.03f * breathe;
                opacity = 0.7f + 0.3f * breathe;
            }
            m_Shield.style.scale = new Scale(new Vector2(scale, scale));
            m_Shield.style.opacity = opacity;
        }
    }
}
