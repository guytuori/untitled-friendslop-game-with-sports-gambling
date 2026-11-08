using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Sets up the Glider's animation (2026-11-08) - runs by itself when the editor loads if it isn't done yet,
    /// or from Friendslop > Player Model > Set Up Glide Animation:
    ///   1. Assets/_models/Mixamo/Glide.fbx is imported as Humanoid (avatar from its own model, like Hanging Idle),
    ///      its one clip renamed "glide", looping, with the root's rotation, height and position baked into the
    ///      pose "based on Original" - the glide pose lies flat, face down, about 1 m up, and without the baking
    ///      Unity would turn that lean into root motion (which is off) and stand the character back up.
    ///   2. A "glide" state with that clip is added to CoreAnimator.controller, in the ActionStates sub-machine next
    ///      to bar_hang_idle, leaving on the ExitAbility trigger like the other ability states. GlideAbility plays
    ///      it (CoreAnimator.ANIMATION_glide).
    /// Nothing is changed when both are already done, so it's safe to run again.
    /// </summary>
    [InitializeOnLoad]
    public static class GlideAnimationSetup
    {
        public const string GlideFbxPath = "Assets/_models/Mixamo/Glide.fbx";
        public const string ControllerPath = "Assets/Core/Art/Animator/CoreAnimator.controller";
        public const string StateName = "glide";
        private const string ActionStatesName = "ActionStates";
        private const string ExitTrigger = "ExitAbility";

        static GlideAnimationSetup()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (NeedsSetup()) Run(verbose: false);
            };
        }

        [MenuItem("Friendslop/Player Model/Set Up Glide Animation")]
        private static void RunFromMenu() => Run(verbose: true);

        private static bool NeedsSetup()
        {
            if (!(AssetImporter.GetAtPath(GlideFbxPath) is ModelImporter importer)) return false; // not imported yet
            if (!ImportIsSetUp(importer)) return true;
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            return controller != null && FindState(controller) == null;
        }

        private static bool ImportIsSetUp(ModelImporter importer)
        {
            if (importer.animationType != ModelImporterAnimationType.Human) return false;
            ModelImporterClipAnimation[] clips = importer.clipAnimations;
            if (clips == null || clips.Length == 0) return false;
            return clips.All(c => c.loopTime && c.lockRootRotation && c.lockRootHeightY && c.lockRootPositionXZ
                                  && c.keepOriginalOrientation && c.keepOriginalPositionY && c.keepOriginalPositionXZ);
        }

        public static void Run(bool verbose)
        {
            if (!(AssetImporter.GetAtPath(GlideFbxPath) is ModelImporter importer))
            {
                Debug.LogWarning($"[Glide] {GlideFbxPath} not found (or not a model) - no glide animation.");
                return;
            }

            // 1. The FBX's import settings.
            if (!ImportIsSetUp(importer))
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.importAnimation = true;

                ModelImporterClipAnimation[] clips = importer.clipAnimations;
                if (clips == null || clips.Length == 0) clips = importer.defaultClipAnimations;
                if (clips == null || clips.Length == 0)
                {
                    Debug.LogWarning($"[Glide] {GlideFbxPath} has no animation in it.");
                    return;
                }
                foreach (ModelImporterClipAnimation clip in clips)
                {
                    if (clips.Length == 1) clip.name = StateName;
                    clip.loopTime = true;
                    clip.loopPose = true;
                    clip.lockRootRotation = true;
                    clip.keepOriginalOrientation = true;
                    clip.lockRootHeightY = true;
                    clip.keepOriginalPositionY = true;
                    clip.lockRootPositionXZ = true;
                    clip.keepOriginalPositionXZ = true;
                }
                importer.clipAnimations = clips;
                importer.SaveAndReimport();
                Debug.Log($"[Glide] {GlideFbxPath}: Humanoid, looping, root baked into the pose.");
            }

            AnimationClip glideClip = AssetDatabase.LoadAllAssetsAtPath(GlideFbxPath)
                .OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
            if (glideClip == null)
            {
                Debug.LogWarning($"[Glide] No animation clip came out of {GlideFbxPath}.");
                return;
            }

            // 2. The controller's state.
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null || controller.layers.Length == 0)
            {
                Debug.LogWarning($"[Glide] {ControllerPath} not found - the glide state wasn't added.");
                return;
            }
            if (!controller.parameters.Any(p => p.name == ExitTrigger))
            {
                Debug.LogWarning($"[Glide] {ControllerPath} has no {ExitTrigger} trigger - the glide state wasn't added.");
                return;
            }

            AnimatorState state = FindState(controller);
            bool added = false;
            if (state == null)
            {
                AnimatorStateMachine root = controller.layers[0].stateMachine;
                AnimatorStateMachine home = root.stateMachines.Select(c => c.stateMachine).FirstOrDefault(m => m.name == ActionStatesName) ?? root;
                state = home.AddState(StateName, new Vector3(420f, 300f, 0f));
                added = true;
            }

            bool changed = added;
            if (state.motion != glideClip)
            {
                state.motion = glideClip;
                changed = true;
            }
            if (!state.transitions.Any(t => t.isExit))
            {
                AnimatorStateTransition exit = state.AddExitTransition();
                exit.hasExitTime = false;
                exit.hasFixedDuration = true;
                exit.duration = 0.1f;
                exit.AddCondition(AnimatorConditionMode.If, 0f, ExitTrigger);
                changed = true;
            }

            if (changed)
            {
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                Debug.Log($"[Glide] {(added ? "Added" : "Updated")} the \"{StateName}\" state in {ControllerPath}.");
            }
            else if (verbose)
            {
                Debug.Log("[Glide] The glide animation is already set up.");
            }
        }

        private static AnimatorState FindState(AnimatorController controller)
        {
            if (controller.layers.Length == 0) return null;
            return FindState(controller.layers[0].stateMachine);
        }

        private static AnimatorState FindState(AnimatorStateMachine machine)
        {
            foreach (ChildAnimatorState child in machine.states)
            {
                if (child.state != null && child.state.name == StateName) return child.state;
            }
            foreach (ChildAnimatorStateMachine sub in machine.stateMachines)
            {
                AnimatorState found = FindState(sub.stateMachine);
                if (found != null) return found;
            }
            return null;
        }
    }
}
