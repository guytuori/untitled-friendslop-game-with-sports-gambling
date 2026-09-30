using Fusion;
using UnityEditor;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Small editor helpers for setting up Photon Fusion components, shared by SceneRefreshSetup and the
    /// one-off FusionMigrationTool.
    /// </summary>
    public static class FusionEditorUtil
    {
        /// <summary>
        /// Makes sure <paramref name="go"/> has a Fusion NetworkObject and that it carries <paramref name="flags"/>.
        /// Returns the NetworkObject. Marks it dirty if anything changed.
        /// </summary>
        public static NetworkObject EnsureNetworkObject(GameObject go, NetworkObjectFlags flags)
        {
            var networkObject = go.GetComponent<NetworkObject>();
            bool changed = false;

            if (networkObject == null)
            {
                networkObject = go.AddComponent<NetworkObject>();
                changed = true;
            }

            if ((networkObject.Flags & flags) != flags)
            {
                networkObject.Flags |= flags;
                changed = true;
            }

            if (changed)
            {
                EditorUtility.SetDirty(networkObject);
            }

            return networkObject;
        }

        /// <summary>
        /// Makes sure <paramref name="go"/> has a Fusion NetworkTransform configured for this project: the owner
        /// moves the transform in Update (CharacterController), so Shared-mode interpolation of the state
        /// authority's own transform is disabled. Returns true if anything changed.
        /// </summary>
        public static bool EnsureNetworkTransform(GameObject go)
        {
            var networkTransform = go.GetComponent<NetworkTransform>();
            bool changed = false;

            if (networkTransform == null)
            {
                networkTransform = go.AddComponent<NetworkTransform>();
                changed = true;
            }

            var flag = NetworkTransform.NetworkTransformFlags.DisableSharedModeInterpolation;
            if ((networkTransform.ConfigFlags & flag) == 0)
            {
                networkTransform.ConfigFlags |= flag;
                changed = true;
            }

            if (changed)
            {
                EditorUtility.SetDirty(networkTransform);
            }

            return changed;
        }

        /// <summary>
        /// Makes sure <paramref name="go"/> has a Fusion NetworkMecanimAnimator driving <paramref name="animator"/>
        /// and syncing all parameters, states and layer weights. Returns true if anything changed.
        /// Fusion bakes the animator hashes itself whenever the prefab/scene is saved.
        /// </summary>
        public static bool EnsureMecanimAnimator(GameObject go, Animator animator)
        {
            var networkAnimator = go.GetComponent<NetworkMecanimAnimator>();
            bool changed = false;

            if (networkAnimator == null)
            {
                networkAnimator = go.AddComponent<NetworkMecanimAnimator>();
                changed = true;
            }

            if (animator != null && networkAnimator.Animator != animator)
            {
                networkAnimator.Animator = animator;
                changed = true;
            }

            var serialized = new SerializedObject(networkAnimator);
            var syncSettings = serialized.FindProperty("SyncSettings");
            const int syncEverything = 0x7F; // ints | floats | bools | triggers | root state | layer states | layer weights
            if (syncSettings != null && syncSettings.intValue != syncEverything)
            {
                syncSettings.intValue = syncEverything;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                changed = true;
            }

            if (changed)
            {
                EditorUtility.SetDirty(networkAnimator);
            }

            return changed;
        }
    }
}
