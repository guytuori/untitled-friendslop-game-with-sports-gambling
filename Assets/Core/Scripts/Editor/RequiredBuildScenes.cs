using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Makes sure menu scenes that are loaded by name are in File > Build Settings (SceneManager.LoadScene
    /// only finds scenes that are listed there). Runs once when the editor loads or scripts recompile, and
    /// adds any missing one right after the scene it belongs next to - no need to edit the list by hand.
    ///
    /// Currently: GameplaySettings (Settings > Gameplay, added 2026-10-12), after Settings.
    /// </summary>
    [InitializeOnLoad]
    public static class RequiredBuildScenes
    {
        private static readonly (string Scene, string After)[] Required =
        {
            ("Assets/Core/Scenes/GameplaySettings.unity", "Assets/Core/Scenes/Settings.unity"),
        };

        static RequiredBuildScenes()
        {
            EditorApplication.delayCall += EnsureListed;
        }

        [MenuItem("Friendslop/Add Missing Menu Scenes To Build")]
        private static void EnsureListed()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            List<EditorBuildSettingsScene> scenes = EditorBuildSettings.scenes.ToList();
            bool changed = false;

            foreach ((string scene, string after) in Required)
            {
                if (scenes.Any(s => s.path == scene)) continue;
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scene) == null) continue; // not imported yet

                int afterIndex = scenes.FindIndex(s => s.path == after);
                var entry = new EditorBuildSettingsScene(scene, true);
                if (afterIndex >= 0) scenes.Insert(afterIndex + 1, entry);
                else scenes.Add(entry);
                changed = true;
                Debug.Log($"[Build Settings] Added {scene} to the scene list.");
            }

            if (changed) EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
