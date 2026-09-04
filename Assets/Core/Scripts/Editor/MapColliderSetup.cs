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
    /// Submeshes whose material name contains "checker" or "bluecarpet" are skipped entirely - those
    /// are balance beams / grind rails (see MapObstacleSetup), and both mechanics deliberately want no
    /// flat walkable collider there: a balance beam needs a round CapsuleCollider with no flat spot,
    /// and a grind rail wants no physical collider at all (it's a proximity-snap mechanic). A flat
    /// MeshCollider generated here would just let the player casually walk across either one, defeating
    /// the whole point. Run MapObstacleSetup AFTER this tool (or re-run this tool after MapObstacleSetup
    /// if you rebuild colliders later - it'll still correctly skip them).
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

        // Keep in sync with MapObstacleSetup's own keyword constants.
        private static readonly string[] NoFlatColliderKeywords = { "checker", "bluecarpet" };

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
            int skipped = 0;
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

                created++;
            }

            EditorUtility.SetDirty(selected);
            Debug.Log($"[Friendslop] Split '{selected.name}' into {created} per-material colliders ({iceSubmeshes} using '{icePhysicMaterial.name}', {skipped} skipped as balance-beam/grind-rail material - see MapObstacleSetup). Remember to save the scene.");
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
