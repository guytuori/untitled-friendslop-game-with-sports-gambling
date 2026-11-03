using System;
using UnityEditor;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Import settings for the How To Play pictures (Assets/Core/Scenes/HowToPlay, added 2026-11-03): kept at their own
    /// size (no stretching to a power of two, which would change a 1920 x 1080 shot's proportions on screen), no
    /// mipmaps, clamped edges - they're full-screen UI pictures. Applied whenever one is added or replaced, so new
    /// screenshots can just be dropped in the folder.
    /// </summary>
    internal class HowToPlayImageImport : AssetPostprocessor
    {
        public const string Folder = "Assets/Core/Scenes/HowToPlay/";

        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) return;
            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Default;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            if (importer.maxTextureSize < 2048) importer.maxTextureSize = 2048;
        }
    }
}
