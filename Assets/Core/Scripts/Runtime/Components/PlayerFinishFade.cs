using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// What happens to a player who reaches the end point - a placeholder until there's a proper animation:
    /// they fade out (and shrink, so it reads even where a material can't be made transparent) over
    /// <see cref="FadeSeconds"/>, then their renderers and collision are switched off. Their own client also
    /// locks their movement for the rest of the round; they can still look around.
    ///
    /// Added at runtime by RoundTimer on every client (no prefab setup), once per finished player.
    /// </summary>
    public class PlayerFinishFade : MonoBehaviour
    {
        public const float FadeSeconds = 1f;
        public const string FinishedInputLock = "Finished";

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private CorePlayerManager m_Player;

        /// <summary>Starts the fade on this player (no-op if already fading).</summary>
        public static void Begin(CorePlayerManager player)
        {
            if (player == null || player.GetComponent<PlayerFinishFade>() != null) return;
            var fade = player.gameObject.AddComponent<PlayerFinishFade>();
            fade.m_Player = player;
        }

        private IEnumerator Start()
        {
            if (m_Player == null) m_Player = GetComponent<CorePlayerManager>();

            if (m_Player != null && m_Player.IsOwner)
            {
                m_Player.SetInputLock(FinishedInputLock, true);
            }

            // Nobody should bump into (or stand on) an invisible player. Movement goes off first, the same
            // way elimination does it (CorePlayerManager), so nothing calls Move on a disabled controller.
            if (m_Player != null && m_Player.CoreMovement != null)
            {
                m_Player.CoreMovement.IsMovementEnabled = false;
                if (m_Player.CoreMovement.TryGetComponent(out CharacterController controller)) controller.enabled = false;
            }

            var renderers = new List<Renderer>();
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (r != null && r.enabled) renderers.Add(r);
            }

            // Per-renderer material copies switched to transparent (URP Lit / Standard), so alpha can fade.
            var materials = new List<(Material Material, int ColorProperty, Color Color)>();
            foreach (Renderer r in renderers)
            {
                foreach (Material m in r.materials) // .materials instantiates copies for this renderer only
                {
                    if (m == null) continue;
                    int colorProperty = m.HasProperty(BaseColorId) ? BaseColorId : m.HasProperty(ColorId) ? ColorId : -1;
                    if (colorProperty == -1) continue;
                    MakeTransparent(m);
                    materials.Add((m, colorProperty, m.GetColor(colorProperty)));
                }
            }

            Transform model = renderers.Count > 0 ? FindModelRoot(renderers) : null;
            Vector3 startScale = model != null ? model.localScale : Vector3.one;

            float t = 0f;
            while (t < FadeSeconds)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / FadeSeconds);
                float alpha = 1f - k;

                foreach (var (material, colorProperty, color) in materials)
                {
                    if (material == null) continue;
                    Color c = color;
                    c.a = color.a * alpha;
                    material.SetColor(colorProperty, c);
                }

                if (model != null) model.localScale = startScale * Mathf.Lerp(1f, 0.05f, k * k);
                yield return null;
            }

            foreach (Renderer r in renderers)
            {
                if (r != null) r.enabled = false;
            }
            if (model != null) model.localScale = startScale;
        }

        /// <summary>The topmost child of the player that holds all of its renderers (the character model), if there is one.</summary>
        private Transform FindModelRoot(List<Renderer> renderers)
        {
            foreach (Transform child in transform)
            {
                bool containsAll = true;
                foreach (Renderer r in renderers)
                {
                    if (!r.transform.IsChildOf(child)) { containsAll = false; break; }
                }
                if (containsAll) return child;
            }
            return null;
        }

        private static void MakeTransparent(Material m)
        {
            // URP Lit / Simple Lit
            if (m.HasProperty("_Surface"))
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                if (m.HasProperty("_SrcBlendAlpha")) m.SetInt("_SrcBlendAlpha", (int)BlendMode.One);
                if (m.HasProperty("_DstBlendAlpha")) m.SetInt("_DstBlendAlpha", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.DisableKeyword("_ALPHATEST_ON");
                m.renderQueue = (int)RenderQueue.Transparent;
                return;
            }

            // Built-in Standard
            if (m.HasProperty("_Mode"))
            {
                m.SetFloat("_Mode", 2f);
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.EnableKeyword("_ALPHABLEND_ON");
                m.renderQueue = (int)RenderQueue.Transparent;
            }
        }
    }
}
