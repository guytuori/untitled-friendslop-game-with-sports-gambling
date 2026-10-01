using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Swaps the visible character model inside the player prefab, keeping everything else (movement,
    /// abilities, networking, camera) as it is.
    ///
    /// Friendslop > Player Model > Use Pirate:
    ///   1. Sets up Pirate.fbx's import settings: Humanoid rig with an explicit bone mapping for the Blender
    ///      rigs made by BlenderProjects/make_*_rigged.py, axis conversion baked in, no animations or cameras,
    ///      and an import scale chosen so the model is as tall as the player's CharacterController.
    ///   2. Replaces the current model in [BB] CorePlayer.prefab with it:
    ///      - the new model's Animator gets the old one's controller and settings (root motion stays off);
    ///      - CoreAnimator moves onto it (the footstep/landing sounds are animation events that call it);
    ///      - everything that pointed at the old model (Fusion's NetworkMecanimAnimator, VisualsAddon,
    ///        CoreAnimator) is re-pointed at the new one, and the model's own Fusion NetworkTransform is kept;
    ///      - VisualsAddon hides every renderer of the new model on elimination. Per-player colours are
    ///        switched off for models made of several pieces (they only work on the placeholder's one mesh).
    ///
    /// Friendslop > Player Model > Use Placeholder (Armature) switches back to the original Unity model.
    /// Both are safe to run repeatedly. A summary is logged to the Console.
    /// </summary>
    public static class PlayerModelSwapTool
    {
        #region Configuration

        private const string PlayerPrefabPath = "Assets/Core/Prefabs/[BB] CorePlayer.prefab";
        private const string PiratePath = "Assets/Core/Art/Models/Characters/Pirate.fbx";
        private const string PlaceholderPath = "Assets/Core/Art/Models/Armature_Core.prefab";

        /// <summary>Used when the player prefab has no CharacterController to measure.</summary>
        private const float DefaultTargetHeight = 1.8f;

        /// <summary>
        /// Unity Humanoid bone -> bone name, for the rigs made by the BlenderProjects/make_*_rigged.py scripts.
        /// </summary>
        private static readonly (string human, string bone)[] BlenderRigMapping =
        {
            ("Hips", "pelvis"),
            ("Spine", "spine"),
            ("Chest", "chest"),
            ("Neck", "neck"),
            ("Head", "head"),
            ("LeftUpperArm", "upper_arm.L"),
            ("LeftLowerArm", "forearm.L"),
            ("LeftHand", "hand.L"),
            ("RightUpperArm", "upper_arm.R"),
            ("RightLowerArm", "forearm.R"),
            ("RightHand", "hand.R"),
            ("LeftUpperLeg", "thigh.L"),
            ("LeftLowerLeg", "shin.L"),
            ("LeftFoot", "foot.L"),
            ("RightUpperLeg", "thigh.R"),
            ("RightLowerLeg", "shin.R"),
            ("RightFoot", "foot.R"),
        };

        #endregion

        #region Menu Items

        [MenuItem("Friendslop/Player Model/Use Pirate")]
        public static void UsePirate()
        {
            SwapPlayerModel(PiratePath, configureAsBlenderRig: true);
        }

        [MenuItem("Friendslop/Player Model/Use Placeholder (Armature)")]
        public static void UsePlaceholder()
        {
            SwapPlayerModel(PlaceholderPath, configureAsBlenderRig: false);
        }

        #endregion

        #region Swap

        /// <summary>
        /// Replaces the model inside the player prefab with the model at <paramref name="modelPath"/> (an FBX or a
        /// prefab with an Animator on its root).
        /// </summary>
        public static void SwapPlayerModel(string modelPath, bool configureAsBlenderRig)
        {
            var log = new StringBuilder();
            try
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(modelPath) == null)
                {
                    Fail($"{modelPath} wasn't found. For the pirate, run BlenderProjects/make_pirate_rigged.py in Blender first - it exports the FBX there.");
                    return;
                }

                if (configureAsBlenderRig)
                {
                    float targetHeight = GetPlayerHeight();
                    if (!ConfigureHumanoidModel(modelPath, targetHeight, log))
                    {
                        return;
                    }
                }

                if (!ReplaceModelInPlayerPrefab(modelPath, log))
                {
                    return;
                }

                Debug.Log($"[Player Model] {PlayerPrefabPath} now uses {modelPath}.\n{log}");
                EditorUtility.DisplayDialog("Player Model", $"The player now uses {System.IO.Path.GetFileNameWithoutExtension(modelPath)}.\n\nDetails are in the Console.", "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Fail($"Stopped with an error: {exception.Message}\n\n{log}");
            }
        }

        private static float GetPlayerHeight()
        {
            var player = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            var controller = player != null ? player.GetComponent<CharacterController>() : null;
            return controller != null ? controller.height : DefaultTargetHeight;
        }

        #endregion

        #region Import Settings

        /// <summary>
        /// Humanoid import with an explicit bone mapping and a scale that makes the model <paramref name="targetHeight"/> tall.
        /// Returns false (after telling the user) if Unity couldn't build a valid Humanoid avatar.
        /// </summary>
        private static bool ConfigureHumanoidModel(string modelPath, float targetHeight, StringBuilder log)
        {
            var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
            if (importer == null)
            {
                Fail($"{modelPath} isn't a model file.");
                return false;
            }

            // Pass 1: basic settings, so the imported hierarchy (and its size) can be measured.
            importer.useFileScale = true;
            importer.bakeAxisConversion = true;
            importer.importAnimation = false;
            importer.importBlendShapes = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importVisibility = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.SaveAndReimport();

            // Scale so the model is as tall as the player's CharacterController.
            float height = MeasureHeight(modelPath);
            if (height > 0.01f && Mathf.Abs(height - targetHeight) > 0.01f)
            {
                importer.globalScale *= targetHeight / height;
                importer.SaveAndReimport();
                log.AppendLine($"Import scale {importer.globalScale:0.###} (model was {height:0.##} m tall, the player is {targetHeight:0.##} m).");
            }

            // Pass 2: explicit Humanoid mapping, against the skeleton exactly as imported (already a T-pose).
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var transforms = model.GetComponentsInChildren<Transform>(true);
            var names = new HashSet<string>(transforms.Select(t => t.name));

            var missing = BlenderRigMapping.Where(m => !names.Contains(m.bone)).Select(m => m.bone).ToList();
            if (missing.Count > 0)
            {
                Fail($"{modelPath} is missing bones the Humanoid avatar needs: {string.Join(", ", missing)}.");
                return false;
            }

            HumanDescription description = importer.humanDescription;
            description.skeleton = transforms
                .Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale })
                .ToArray();
            description.human = BlenderRigMapping
                .Select(m => new HumanBone { humanName = m.human, boneName = m.bone, limit = new HumanLimit { useDefaultValues = true } })
                .ToArray();
            description.hasTranslationDoF = false;
            if (description.upperArmTwist == 0f) description.upperArmTwist = 0.5f;
            if (description.lowerArmTwist == 0f) description.lowerArmTwist = 0.5f;
            if (description.upperLegTwist == 0f) description.upperLegTwist = 0.5f;
            if (description.lowerLegTwist == 0f) description.lowerLegTwist = 0.5f;
            if (description.armStretch == 0f) description.armStretch = 0.05f;
            if (description.legStretch == 0f) description.legStretch = 0.05f;

            importer.humanDescription = description;
            importer.SaveAndReimport();

            Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                Fail($"Unity couldn't build a Humanoid avatar for {modelPath}. Select it and open Rig > Configure to see why.");
                return false;
            }

            // Unity's Humanoid expects the T-pose to face +Z (so the character's left side is at -X). A model
            // facing the other way plays every animation backwards with its legs crossed over.
            Transform leftLeg = transforms.First(t => t.name == "thigh.L");
            Transform rightLeg = transforms.First(t => t.name == "thigh.R");
            if (leftLeg.position.x > rightLeg.position.x)
            {
                Fail($"{modelPath} faces backwards (-Z): its left leg is on the +X side. Turn the model 180 degrees in Blender and re-export it.");
                return false;
            }

            log.AppendLine($"{modelPath}: Humanoid avatar OK ({BlenderRigMapping.Length} bones mapped, facing +Z).");
            return true;
        }

        private static float MeasureHeight(string modelPath)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var instance = Object.Instantiate(asset);
            try
            {
                // Keep the model's own root rotation: Blender exports carry one (it's what stands the model up),
                // and resetting it here measured the model lying on its side.
                instance.transform.position = Vector3.zero;
                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) return 0f;

                Bounds bounds = renderers[0].bounds;
                foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
                return bounds.max.y - Mathf.Min(0f, bounds.min.y);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        #endregion

        #region Prefab Edit

        private static bool ReplaceModelInPlayerPrefab(string modelPath, StringBuilder log)
        {
            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            GameObject root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            try
            {
                // ---- Find the current model: the object CoreAnimator lives on (the placeholder's root).
                var oldCore = root.GetComponentInChildren<CoreAnimator>(true);
                if (oldCore == null || oldCore.gameObject == root)
                {
                    Fail($"Couldn't find the current model in {PlayerPrefabPath} (no CoreAnimator on a child object).");
                    return false;
                }

                GameObject oldModel = oldCore.gameObject;
                if (PrefabUtility.IsPartOfPrefabInstance(oldModel))
                {
                    GameObject instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(oldModel);
                    if (instanceRoot != null && instanceRoot != root) oldModel = instanceRoot;
                }

                Object currentSource = PrefabUtility.GetCorrespondingObjectFromSource(oldModel);
                if (currentSource != null && AssetDatabase.GetAssetPath(currentSource) == modelPath)
                {
                    log.AppendLine("The player already uses this model - only refreshing its settings.");
                }

                Animator oldAnimator = oldModel.GetComponentInChildren<Animator>(true);
                bool oldHadNetworkTransform = oldModel.GetComponent<Fusion.NetworkTransform>() != null;

                // ---- Add the new model in the same place.
                var newModel = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset, oldModel.transform.parent);
                newModel.transform.SetSiblingIndex(oldModel.transform.GetSiblingIndex());
                // Same place as the old model, keeping the new model's own root rotation/scale (a Blender
                // export's root carries the rotation that stands it up).
                newModel.transform.localPosition = oldModel.transform.localPosition;
                newModel.transform.localRotation = modelAsset.transform.localRotation;
                newModel.transform.localScale = modelAsset.transform.localScale;

                Animator newAnimator = newModel.GetComponent<Animator>();
                if (newAnimator == null) newAnimator = newModel.AddComponent<Animator>();
                if (newAnimator.avatar == null)
                {
                    newAnimator.avatar = AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>().FirstOrDefault();
                }
                if (oldAnimator != null)
                {
                    newAnimator.runtimeAnimatorController = oldAnimator.runtimeAnimatorController;
                    newAnimator.updateMode = oldAnimator.updateMode;
                    newAnimator.cullingMode = oldAnimator.cullingMode;
                }
                newAnimator.applyRootMotion = false;

                // ---- CoreAnimator moves with the Animator (animation events call it on the Animator's object).
                CoreAnimator newCore = newModel.GetComponent<CoreAnimator>();
                if (newCore == null) newCore = newModel.AddComponent<CoreAnimator>();
                EditorUtility.CopySerialized(oldCore, newCore);

                if (oldHadNetworkTransform)
                {
                    FusionEditorUtil.EnsureNetworkTransform(newModel);
                }

                // ---- Re-point everything that referenced the old model.
                int remapped = RemapReferences(root, oldModel, newModel, oldAnimator, newAnimator, oldCore, newCore, log);
                log.AppendLine($"Re-pointed {remapped} reference(s) from the old model to the new one.");

                // ---- VisualsAddon: hide the whole new model on elimination; per-player colours only for single-mesh models.
                Renderer[] renderers = newModel.GetComponentsInChildren<Renderer>(true);
                foreach (VisualsAddon visuals in root.GetComponentsInChildren<VisualsAddon>(true))
                {
                    var serialized = new SerializedObject(visuals);

                    var hide = serialized.FindProperty("hideWhenEliminated");
                    if (hide != null)
                    {
                        hide.arraySize = renderers.Length;
                        for (int i = 0; i < renderers.Length; i++) hide.GetArrayElementAtIndex(i).objectReferenceValue = renderers[i];
                    }

                    var target = serialized.FindProperty("targetRenderer");
                    if (target != null)
                    {
                        target.objectReferenceValue = renderers.Length == 1 ? renderers[0] : null;
                    }

                    var animatorProperty = serialized.FindProperty("playerAnimator");
                    if (animatorProperty != null) animatorProperty.objectReferenceValue = newAnimator;

                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    log.AppendLine(renderers.Length == 1
                        ? "VisualsAddon: per-player colours on, hides the model's mesh on elimination."
                        : $"VisualsAddon: hides all {renderers.Length} model pieces on elimination; per-player colours off (they need a single-mesh model).");
                }

                // CoreAnimator must point at the new Animator.
                var coreSerialized = new SerializedObject(newCore);
                var coreAnimatorField = coreSerialized.FindProperty("m_Animator");
                if (coreAnimatorField != null) coreAnimatorField.objectReferenceValue = newAnimator;
                coreSerialized.ApplyModifiedPropertiesWithoutUndo();

                // ---- Remove the old model and save.
                string oldName = oldModel.name;
                Object.DestroyImmediate(oldModel);
                newModel.name = modelAsset.name;

                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
                log.AppendLine($"Replaced '{oldName}' with '{newModel.name}' in {PlayerPrefabPath}.");
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Every serialized object reference (outside the old model) that points into the old model is re-pointed:
        /// its Animator/CoreAnimator/root to the new ones, any other transform to the same-named transform in
        /// the new model if there is one, otherwise cleared (and reported).
        /// </summary>
        private static int RemapReferences(GameObject root, GameObject oldModel, GameObject newModel,
            Animator oldAnimator, Animator newAnimator, CoreAnimator oldCore, CoreAnimator newCore, StringBuilder log)
        {
            Transform oldRoot = oldModel.transform;
            var newByName = new Dictionary<string, Transform>();
            foreach (Transform t in newModel.GetComponentsInChildren<Transform>(true))
            {
                if (!newByName.ContainsKey(t.name)) newByName.Add(t.name, t);
            }

            int count = 0;
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component.transform.IsChildOf(oldRoot)) continue;
                // Hierarchy links are Unity's business, and Fusion re-bakes NetworkObject's behaviour list on save.
                if (component is Transform || component is Fusion.NetworkObject) continue;

                var serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();
                bool changed = false;

                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (property.name == "m_Script" || property.name == "m_GameObject" || property.name == "m_Father") continue;

                    Object value = property.objectReferenceValue;
                    if (value == null) continue;

                    Transform valueTransform = value is Component c ? c.transform : value is GameObject g ? g.transform : null;
                    if (valueTransform == null || !valueTransform.IsChildOf(oldRoot)) continue;

                    Object replacement;
                    if (value == oldAnimator) replacement = newAnimator;
                    else if (value == oldCore) replacement = newCore;
                    else if (value == oldModel) replacement = newModel;
                    else if (value == oldRoot) replacement = newModel.transform;
                    else if (value is Renderer) replacement = null; // VisualsAddon's renderer fields are rebuilt afterwards
                    else if (newByName.TryGetValue(valueTransform.name, out Transform match))
                    {
                        replacement = value is GameObject ? (Object)match.gameObject
                            : value is Transform ? match
                            : match.GetComponent(value.GetType());
                    }
                    else replacement = null;

                    if (replacement == null && !(value is Renderer))
                    {
                        log.AppendLine($"  Cleared {component.GetType().Name}.{property.propertyPath} on '{component.name}' (pointed at '{value.name}' in the old model, which the new one doesn't have).");
                    }

                    property.objectReferenceValue = replacement;
                    changed = true;
                    count++;
                }

                if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            return count;
        }

        #endregion

        #region Helpers

        private static void Fail(string message)
        {
            Debug.LogError($"[Player Model] {message}");
            EditorUtility.DisplayDialog("Player Model", message, "OK");
        }

        #endregion
    }
}
