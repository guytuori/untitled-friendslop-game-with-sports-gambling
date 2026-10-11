using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Animated map tiles: anything drawn with a tile texture named <c>something_1</c> (water_1) flips to
    /// <c>something_2</c> (water_2) and back every <see cref="FrameSeconds"/> seconds. Which tiles animate
    /// comes from <see cref="AnimatedTileCatalog"/> (built automatically from Assets/Maps), so a new animated
    /// tile is just a _1 and _2 image pair in Total Editor - nothing to set up in Unity.
    ///
    /// How: when a scene loads and whenever MatchLayout finishes building a map, every renderer is checked;
    /// each material showing a _1 texture is swapped for a runtime copy (one copy per original material, so
    /// everything using it still flips together), and those copies have their texture changed on the beat.
    /// The imported materials themselves are never touched.
    ///
    /// Runs on a hidden object made at startup; no scene setup.
    /// </summary>
    public static class AnimatedTiles
    {
        /// <summary>Seconds each frame shows for.</summary>
        public const float FrameSeconds = 1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void CreateRunner()
        {
            var go = new GameObject("[AnimatedTiles]") { hideFlags = HideFlags.HideInHierarchy };
            Object.DontDestroyOnLoad(go);
            go.AddComponent<AnimatedTilesRunner>();
        }
    }

    [AddComponentMenu("")]
    internal sealed class AnimatedTilesRunner : MonoBehaviour
    {
        private sealed class AnimatedMaterial
        {
            public Material Copy;
            public readonly List<(int Property, Texture First, Texture Second)> Slots = new List<(int, Texture, Texture)>();
        }

        private AnimatedTileCatalog m_Catalog;
        private bool m_CatalogLoaded;

        /// <summary>Original material -> its animated copy (null when the original has nothing to animate).</summary>
        private readonly Dictionary<Material, AnimatedMaterial> m_ByOriginal = new Dictionary<Material, AnimatedMaterial>();
        private readonly HashSet<Material> m_Copies = new HashSet<Material>();
        private readonly List<AnimatedMaterial> m_Animated = new List<AnimatedMaterial>();
        private int m_ShownFrame;
        private bool m_ScanPending = true;

        private void OnEnable()
        {
            SceneManager.sceneLoaded += HandleSceneLoaded;
            MatchLayout.Built += HandleMapBuilt;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            MatchLayout.Built -= HandleMapBuilt;
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single) Clear();
            m_ScanPending = true;
        }

        private void HandleMapBuilt(MatchLayout layout) => m_ScanPending = true;

        private void LateUpdate()
        {
            if (m_ScanPending)
            {
                m_ScanPending = false;
                Scan();
            }

            if (m_Animated.Count == 0) return;

            int frame = (int)(Time.time / AnimatedTiles.FrameSeconds) & 1;
            if (frame == m_ShownFrame) return;
            m_ShownFrame = frame;
            foreach (AnimatedMaterial animated in m_Animated) Show(animated, frame);
        }

        private void Scan()
        {
            if (!m_CatalogLoaded)
            {
                m_CatalogLoaded = true;
                m_Catalog = AnimatedTileCatalog.Load();
            }
            if (m_Catalog == null || m_Catalog.Tiles.Count == 0) return;

            int swapped = 0;
            foreach (Renderer renderer in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer) continue;

                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material material = materials[i];
                    if (material == null || m_Copies.Contains(material)) continue;

                    AnimatedMaterial animated = GetOrMake(material);
                    if (animated == null) continue;

                    materials[i] = animated.Copy;
                    changed = true;
                }

                if (changed)
                {
                    renderer.sharedMaterials = materials;
                    swapped++;
                }
            }

            if (swapped > 0) Debug.Log($"[AnimatedTiles] Animating {m_Animated.Count} material(s) on {swapped} more object(s).");
        }

        private AnimatedMaterial GetOrMake(Material original)
        {
            if (m_ByOriginal.TryGetValue(original, out AnimatedMaterial known)) return known;

            AnimatedMaterial animated = null;
            foreach (int property in original.GetTexturePropertyNameIDs())
            {
                Texture texture = original.GetTexture(property);
                if (texture == null || !m_Catalog.TryGetSecondFrame(texture, out Texture second) || second == texture) continue;

                if (animated == null)
                {
                    animated = new AnimatedMaterial { Copy = new Material(original) { name = original.name + " (animated)" } };
                }
                animated.Slots.Add((property, texture, second));
            }

            m_ByOriginal[original] = animated;
            if (animated != null)
            {
                m_Copies.Add(animated.Copy);
                m_Animated.Add(animated);
                Show(animated, m_ShownFrame);
            }
            return animated;
        }

        private static void Show(AnimatedMaterial animated, int frame)
        {
            if (animated.Copy == null) return;
            foreach ((int property, Texture first, Texture second) in animated.Slots)
            {
                animated.Copy.SetTexture(property, frame == 0 ? first : second);
            }
        }

        private void Clear()
        {
            foreach (AnimatedMaterial animated in m_Animated)
            {
                if (animated.Copy != null) Destroy(animated.Copy);
            }
            m_Animated.Clear();
            m_Copies.Clear();
            m_ByOriginal.Clear();
        }
    }
}
