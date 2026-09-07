using System.IO;
using UnityEngine;
using UnityEditor;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One-time setup helper that splits a glTF-imported map's single combined MeshCollider into one
    /// MeshCollider per submesh (i.e. per material/texture), so different parts of a map can have
    /// their own PhysicMaterial - most importantly, low-friction ice.
    ///
    /// Why this is needed: Unity's MeshCollider only supports a single PhysicMaterial for the whole
    /// collider, even when the render mesh has multiple submeshes/materials (which is how Total
    /// Editor 3 exports a map - one mesh, one submesh per texture). This tool works around that by
    /// giving each submesh its own small collider GameObject with its own MeshCollider, so each one
    /// can carry its own PhysicMaterial.
    ///
    /// A submesh whose material name contains "ice" (case-insensitive - matches texture paths like
    /// "assets/textures/tiles/ice.png") gets IcePhysicMaterialPath assigned. Everything else is left
    /// with no PhysicMaterial (Unity's project default friction), so non-ice parts of the map behave
    /// exactly as before.
    ///
    /// Submeshes whose material name contains "checker", "bluecarpet" or "pillar" are skipped entirely -
    /// those are balance beams / grind rails / fireman's poles (see MapObstacleSetup), and all three
    /// mechanics deliberately want no flat walkable collider there: a balance beam needs a round
    /// CapsuleCollider with no flat spot, a grind rail wants no physical collider at all (it's a
    /// proximity-snap mechanic), and a pole needs its own vertical CapsuleCollider (see Pole) rather
    /// than whatever flat shape this tool would otherwise build from the pillar's render mesh. A flat
    /// MeshCollider generated here would just let the player casually walk across a beam/rail, or give
    /// a pole a lumpy, non-cylindrical collision shape instead of the clean capsule PoleGrabAbility
    /// expects. Run MapObstacleSetup AFTER this tool (or re-run this tool after MapObstacleSetup
    /// if you rebuild colliders later - it'll still correctly skip them).
    ///
    /// A submesh whose material name contains "brickwall" is treated differently: unlike the three
    /// above, it's NOT skipped - it still gets an ordinary flat MeshCollider like any other wall, since
    /// it should keep blocking the player normally. It additionally gets a <see cref="ClimbableWall"/>
    /// marker component added to its collider GameObject, which is what WallClimbAbility checks to
    /// decide whether a given wall can be climbed (see that class). This is how climbable surfaces are
    /// split out as their own thing, the same way checker/bluecarpet/pillar are split into
    /// beams/rails/poles - just without needing a special collider shape, since a wall's own flat
    /// collider is already the right shape to climb.
    ///
    /// A submesh whose material name contains "start" or "finish" gets the same ordinary flat
    /// MeshCollider too (it's still walkable floor), plus a second, invisible trigger volume covering
    /// that tile area's full airspace - not just its floor - so jumping over it counts the same as
    /// walking through it (see ChallengeZoneTrigger). The map's root GameObject gets a
    /// <see cref="ChallengeZone"/> the first time this runs (reused on later runs), with a brand new
    /// <see cref="ChallengeDefinition"/> asset if it doesn't have one yet - see EnsureChallengeZone.
    /// Since Total Editor 3 exports one submesh per texture, "the contiguous start area" and "the
    /// contiguous finish area" the challenge design calls for are already exactly one submesh each,
    /// with no extra grouping work needed here.
    ///
    /// Usage: select the map's GameObject INSTANCE in the Hierarchy (drag the .gltf asset into the
    /// scene first if it isn't there yet - this won't work run on the asset itself in the Project
    /// window, since glTF/model imports don't allow hand-added components to be saved onto them).
    /// It needs a MeshFilter + MeshRenderer (added automatically by the glTF import). Then run
    /// Friendslop > Split Selected Map Collider By Material.
    ///
    /// Safe to re-run: any "Collider_*" children and any single whole-mesh MeshCollider from a
    /// previous run (or from UnityGLTF's own "Add Colliders" import setting) are removed and rebuilt
    /// from scratch each time.
    /// </summary>
    public static class MapColliderSetup
    {
        private const string IceKeyword = "ice";
        private const string IcePhysicMaterialPath = "Assets/Maps/PM_Ice.physicMaterial";
        private const string ColliderChildPrefix = "Collider_";

        // Keep in sync with WallClimbAbility/ClimbableWall's own expectations.
        private const string ClimbableKeyword = "brickwall";

        // Keep in sync with MapObstacleSetup's own keyword constants.
        private static readonly string[] NoFlatColliderKeywords = { "checker", "bluecarpet", "pillar" };

        // Keep in sync with ChallengeZone/ChallengeZoneTrigger's own expectations.
        private const string StartKeyword = "start";
        private const string FinishKeyword = "finish";
        private const string ChallengeTriggerChildPrefix = "ChallengeTrigger_";
        private const float ChallengeTriggerHeight = 4f;
        private const string ChallengeDefinitionFolder = "Assets/Core/Data/Challenges";

        [MenuItem("Friendslop/Split Selected Map Collider By Material")]
        public static void SplitSelectedMapCollider()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogError("[Friendslop] Select the map's GameObject in the Hierarchy first (the instance with the MeshFilter/MeshRenderer - drag the .gltf into the scene if it isn't there yet).");
                return;
            }

            var meshFilter = selected.GetComponent<MeshFilter>();
            var meshRenderer = selected.GetComponent<MeshRenderer>();
            if (meshFilter == null || meshFilter.sharedMesh == null || meshRenderer == null)
            {
                Debug.LogError($"[Friendslop] '{selected.name}' needs a MeshFilter (with a mesh assigned) and a MeshRenderer.");
                return;
            }

            Mesh sourceMesh = meshFilter.sharedMesh;
            Material[] materials = meshRenderer.sharedMaterials;

            if (materials.Length != sourceMesh.subMeshCount)
            {
                Debug.LogWarning($"[Friendslop] '{selected.name}' has {sourceMesh.subMeshCount} submeshes but {materials.Length} materials assigned - results may be mismatched.");
            }

            var icePhysicMaterial = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(IcePhysicMaterialPath);
            if (icePhysicMaterial == null)
            {
                Debug.LogError($"[Friendslop] Couldn't find the ice PhysicMaterial at '{IcePhysicMaterialPath}'. Has it moved or been renamed?");
                return;
            }

            // Remove per-submesh colliders from a previous run of this tool.
            for (int i = selected.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = selected.transform.GetChild(i);
                if (child.name.StartsWith(ColliderChildPrefix))
                {
                    Object.DestroyImmediate(child.gameObject);
                }
            }

            // Remove the single whole-mesh collider (e.g. from UnityGLTF's "Add Colliders" import
            // setting) so it doesn't double up collision with the new per-submesh ones.
            var wholeCollider = selected.GetComponent<MeshCollider>();
            if (wholeCollider != null)
            {
                Object.DestroyImmediate(wholeCollider);
            }

            int created = 0;
            int iceSubmeshes = 0;
            int climbableSubmeshes = 0;
            int challengeSubmeshes = 0;
            int skipped = 0;
            ChallengeZone challengeZone = null;
            for (int sub = 0; sub < sourceMesh.subMeshCount; sub++)
            {
                Material mat = sub < materials.Length ? materials[sub] : null;
                string matName = mat != null ? mat.name : $"submesh{sub}";
                string matNameLower = matName.ToLowerInvariant();

                bool skipFlatCollider = false;
                foreach (string keyword in NoFlatColliderKeywords)
                {
                    if (matNameLower.Contains(keyword))
                    {
                        skipFlatCollider = true;
                        break;
                    }
                }

                if (skipFlatCollider)
                {
                    skipped++;
                    continue;
                }

                var colliderObj = new GameObject($"{ColliderChildPrefix}{matName}");
                colliderObj.transform.SetParent(selected.transform, false);

                var collider = colliderObj.AddComponent<MeshCollider>();
                collider.sharedMesh = ExtractSubMesh(sourceMesh, sub);

                bool isIce = matNameLower.Contains(IceKeyword);
                if (isIce)
                {
                    collider.sharedMaterial = icePhysicMaterial;
                    iceSubmeshes++;
                }

                bool isClimbable = matNameLower.Contains(ClimbableKeyword);
                if (isClimbable)
                {
                    colliderObj.AddComponent<ClimbableWall>();
                    climbableSubmeshes++;
                }

                bool isStart = matNameLower.Contains(StartKeyword);
                bool isFinish = matNameLower.Contains(FinishKeyword);
                if (isStart || isFinish)
                {
                    if (challengeZone == null)
                    {
                        challengeZone = EnsureChallengeZone(selected);
                    }

                    if (challengeZone != null)
                    {
                        ChallengeZoneKind kind = isStart ? ChallengeZoneKind.Start : ChallengeZoneKind.Finish;
                        CreateChallengeTrigger(colliderObj, collider.sharedMesh, challengeZone, kind);
                        challengeSubmeshes++;
                    }
                }

                created++;
            }

            EditorUtility.SetDirty(selected);
            Debug.Log($"[Friendslop] Split '{selected.name}' into {created} per-material colliders ({iceSubmeshes} using '{icePhysicMaterial.name}', {climbableSubmeshes} tagged '{nameof(ClimbableWall)}' from '{ClimbableKeyword}' material, {challengeSubmeshes} wired up as challenge start/finish triggers, {skipped} skipped as balance-beam/grind-rail/pole material - see MapObstacleSetup). Remember to save the scene." +
                (climbableSubmeshes > 0 ? " Make sure the player prefab has WallClimbAbility on it too, and that its wallLayers includes whatever layer these colliders are on." : "") +
                (challengeSubmeshes > 0 ? $" Check '{selected.name}''s new ChallengeZone component for its ChallengeDefinition asset and tweak its name/points/pars." : ""));
        }

        /// <summary>
        /// Gets or adds the map instance's ChallengeZone, creating it a brand new ChallengeDefinition asset
        /// (named after the map instance, with the class defaults - 100 points, par 3 deaths for +25, par
        /// 10s for +25) the first time this runs. Re-running this tool on the same instance reuses both -
        /// your edits to the definition's name/points/pars are never overwritten.
        /// </summary>
        private static ChallengeZone EnsureChallengeZone(GameObject selected)
        {
            var zone = selected.GetComponent<ChallengeZone>();
            if (zone == null)
            {
                zone = selected.AddComponent<ChallengeZone>();
            }

            var serializedZone = new SerializedObject(zone);
            var definitionProperty = serializedZone.FindProperty("definition");
            if (definitionProperty.objectReferenceValue == null)
            {
                if (!AssetDatabase.IsValidFolder(ChallengeDefinitionFolder))
                {
                    Directory.CreateDirectory(ChallengeDefinitionFolder);
                    AssetDatabase.Refresh();
                }

                var definition = ScriptableObject.CreateInstance<ChallengeDefinition>();
                definition.challengeName = selected.name;

                string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{ChallengeDefinitionFolder}/{selected.name}Definition.asset");
                AssetDatabase.CreateAsset(definition, assetPath);
                AssetDatabase.SaveAssets();

                definitionProperty.objectReferenceValue = definition;
                serializedZone.ApplyModifiedProperties();

                Debug.Log($"[Friendslop] Created '{assetPath}' for '{selected.name}' - edit its name/points/pars there.");
            }

            return zone;
        }

        /// <summary>
        /// Builds one invisible BoxCollider trigger covering the given submesh's full footprint, extended
        /// upward through ChallengeTriggerHeight of airspace, and wires it to fire into the given zone.
        /// Parented under the submesh's own walkable MeshCollider GameObject purely so it gets cleaned up
        /// automatically the next time this tool re-runs (that GameObject is destroyed and rebuilt every run).
        /// </summary>
        private static void CreateChallengeTrigger(GameObject colliderObj, Mesh subMesh, ChallengeZone zone, ChallengeZoneKind kind)
        {
            var triggerObj = new GameObject($"{ChallengeTriggerChildPrefix}{kind}");
            triggerObj.transform.SetParent(colliderObj.transform, false);

            Bounds bounds = subMesh.bounds;
            var box = triggerObj.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = bounds.center + new Vector3(0f, ChallengeTriggerHeight * 0.5f, 0f);
            box.size = new Vector3(bounds.size.x, ChallengeTriggerHeight, bounds.size.z);

            var relay = triggerObj.AddComponent<ChallengeZoneTrigger>();
            relay.Configure(zone, kind);
        }

        /// <summary>
        /// Builds a standalone mesh containing only the given submesh's triangles. Keeps the full
        /// original vertex buffer rather than trimming to just the referenced vertices - simplest and
        /// always correct, and the extra unused vertices are harmless for a collision-only mesh.
        /// </summary>
        private static Mesh ExtractSubMesh(Mesh source, int subMeshIndex)
        {
            var subMesh = new Mesh
            {
                name = $"{source.name}_collider_{subMeshIndex}",
                indexFormat = source.indexFormat,
                vertices = source.vertices,
                triangles = source.GetTriangles(subMeshIndex)
            };
            subMesh.RecalculateBounds();
            return subMesh;
        }
    }
}
