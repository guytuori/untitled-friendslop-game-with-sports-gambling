using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// TEMPORARY dev toggle: forces a flat, fully-lit look with no shadows across every scene, without
    /// hand-editing any scene file. (The main gameplay scene - "[BB] Core.unity" - is currently far too
    /// large to open/edit outside the Unity Editor, so this can't be done by changing its baked
    /// RenderSettings/Light values directly; it's enforced at runtime instead.)
    ///
    /// What this does, every time a scene loads (initial load and any additive/scene-switch load after
    /// it, so procedurally-loaded maps are covered too):
    /// - Sets ambient lighting to flat, full-intensity white, so surfaces aren't relying on baked/GI
    ///   ambient to look lit.
    /// - Turns off shadow casting on every Light in the scene.
    /// - Zeroes reflection probe intensity, so reflective materials don't pick up dark/baked reflections
    ///   that would otherwise fight the flat-lit look.
    ///
    /// This is a non-destructive, runtime-only override - it never modifies a scene, prefab or light
    /// asset on disk. Shadow rendering is also turned off at the pipeline level in PC_RPAsset.asset
    /// (Main/Additional Light Shadows and Any Shadows all disabled) and the SSAO renderer feature is
    /// disabled in PC_Renderer.asset, so shadow maps and ambient occlusion aren't even computed - this
    /// script is what handles the ambient/GI brightness side on top of that.
    ///
    /// To turn normal lighting back on later: set <see cref="Enabled"/> to false, or delete this file.
    /// Nothing else needs to change.
    /// </summary>
    public static class FullBrightMode
    {
        /// <summary>Flip to false (or delete this file) to stop overriding lighting and go back to however the scene was authored.</summary>
        private const bool Enabled = true;

        private static bool s_ListenerRegistered;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void OnFirstSceneLoaded()
        {
            if (!Enabled) return;

            Apply(SceneManager.GetActiveScene());

            if (!s_ListenerRegistered)
            {
                SceneManager.sceneLoaded += (scene, mode) => Apply(scene);
                s_ListenerRegistered = true;
            }
        }

        private static void Apply(Scene scene)
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.white;
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.reflectionIntensity = 0f;

            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                light.shadows = LightShadows.None;
            }
        }
    }
}
