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
    /// Submeshes whose material name contains "balance_beam", "grind_rail", "pole" or
    /// "player_spawn_point" are skipped entirely - the first three are balance beams / grind rails /
    /// fireman's poles (see MapObstacleSetup), and all three mechanics deliberately want no flat
    /// walkable collider there: a balance beam needs a round CapsuleCollider with no flat spot, a
    /// grind rail wants no physical collider at all (it's a proximity-snap mechanic), and a pole needs
    /// its own vertical CapsuleCollider (see Pole) rather than whatever flat shape this tool would
    /// otherwise build from the pole's render mesh. A flat MeshCollider generated here would just let
    /// the player casually walk across a beam/rail, or give a pole a lumpy, non-cylindrical collision
    /// shape instead of the clean capsule PoleGrabAbility expects. "player_spawn_point" tiles want no
    /// collider at all either - they're not something a player should ever stand or walk on, just a
    /// marker for where GameManager places a player on spawn (see MapObstacleSetup's
    /// PlayerSpawnPoint handling), so they get skipped here too, in addition to being made invisible
    /// (see below). Run MapObstacleSetup AFTER this tool (or re-run this tool after MapObstacleSetup
    /// if you rebuild colliders later - it'll still correctly skip them).
    ///
    /// A submesh whose material name contains "player_spawn_point" also has its material swapped to a
    /// fully transparent one (created once at InvisibleMaterialPath and reused thereafter - see
    /// EnsureInvisibleMaterial), so the tile itself is invisible in the finished map instead of showing
    /// its placeholder floor texture. Combined with the no-collider treatment above, this makes the
    /// tile purely a positional marker with no visible or physical presence.
    ///
    /// A submesh whose material name contains "wall_climb" is treated differently: unlike the three
    /// above, it's NOT skipped - it still gets an ordinary flat MeshCollider like any other wall, since
    /// it should keep blocking the player normally. It additionally gets a <see cref="ClimbableWall"/>
    /// marker component added to its collider GameObject, which is what WallClimbAbility checks to
    /// decide whether a given wall can be climbed (see that class). This is how climbable surfaces are
    /// split out as their own thing, the same way balance_beam/grind_rail/pole are split into
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
        private const string WallClimbKeyword = "wall_climb";

        // Keep in sync with MapObstacleSetup's PlayerSpawnPoint handling.
        private const string PlayerSpawnPointKeyword = "player_spawn_point";
        private const string InvisibleMaterialPath = "Assets/Maps/M_SpawnPointInvisible.mat";

        // Keep in sync with MapObstacleSetup's own keyword constants.
        private static readonly string[] NoFlatColliderKeywords = { "balance_beam", "grind_rail", "pole", PlayerSpawnPointKeyword };

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

            RunOnMapObject(selected);
        }

        /// <summary>
        /// The actual per-map-piece logic, callable directly against a known GameObject rather than only
        /// via the current Hierarchy selection - see SceneRefreshSetup (Friendslop > Refresh Everything
        /// From Assets), which finds every map piece in the scene structurally and calls this on each one
        /// without anything needing to be selected first.
        /// </summary>
        public static void RunOnMapObject(GameObject selected)
        {
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

            Material invisibleMaterial = EnsureInvisibleMaterial();
            bool materialsChanged = false;

            int created = 0;
            int iceSubmeshes = 0;
            int climbableSubmeshes = 0;
            int challengeSubmeshes = 0;
            int spawnPointSubmeshes = 0;
            int skipped = 0;
            ChallengeZone challengeZone = null;
            for (int sub = 0; sub < sourceMesh.subMeshCount; sub++)
            {
                Material mat = sub < materials.Length ? materials[sub] : null;
                string matName = mat != null ? mat.name : $"submesh{sub}";
                string matNameLower = matName.ToLowerInvariant();

                bool isSpawnPoint = matNameLower.Contains(PlayerSpawnPointKeyword);
                if (isSpawnPoint && invisibleMaterial != null && sub < materials.Length && materials[sub] != invisibleMaterial)
                {
                    materials[sub] = invisibleMaterial;
                    materialsChanged = true;
                    spawnPointSubmeshes++;
                }

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

                bool isClimbable = matNameLower.Contains(WallClimbKeyword);
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

            if (materialsChanged)
            {
                meshRenderer.sharedMaterials = materials;
            }

            EditorUtility.SetDirty(selected);
            Debug.Log($"[Friendslop] Split '{selected.name}' into {created} per-material colliders ({iceSubmeshes} using '{icePhysicMaterial.name}', {climbableSubmeshes} tagged '{nameof(ClimbableWall)}' from '{WallClimbKeyword}' material, {challengeSubmeshes} wired up as challenge start/finish triggers, {spawnPointSubmeshes} '{PlayerSpawnPointKeyword}' tile(s) made invisible, {skipped} skipped as balance-beam/grind-rail/pole/player_spawn_point material - see MapObstacleSetup). Remember to save the scene." +
                (climbableSubmeshes > 0 ? " Make sure the player prefab has WallClimbAbility on it too, and that its wallLayers includes whatever layer these colliders are on." : "") +
                (challengeSubmeshes > 0 ? $" Check '{selected.name}''s new ChallengeZone component for its ChallengeDefinition asset and tweak its name/points/pars." : "") +
                (spawnPointSubmeshes > 0 ? " Run Friendslop > Build Balance Beams And Grind Rails And Poles From Map (or Refresh Everything From Assets) afterward so PlayerSpawnPoint markers get created for these tiles." : ""));
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
        /// Gets or creates the fully transparent Material used to hide "player_spawn_point" tiles,
        /// created once at InvisibleMaterialPath and reused on every later run - the same create-once
        /// pattern EnsureChallengeZone uses for its ChallengeDefinition asset. Handles both URP (this
        /// project's render pipeline) and the built-in Standard shader as a fallback, in case URP isn't
        /// available for some reason when this runs.
        /// </summary>
        private static Material EnsureInvisibleMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(InvisibleMaterialPath);
            if (existing != null)
            {
                return existing;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            bool isUrp = shader != null;
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                Debug.LogError("[Friendslop] Couldn't find a URP Lit or Standard shader to build the invisible spawn point material - player_spawn_point tiles will keep their placeholder texture.");
                return null;
            }

            var material = new Material(shader) { name = "M_SpawnPointInvisible" };
            Color transparent = new Color(1f, 1f, 1f, 0f);

            if (isUrp)
            {
                material.SetFloat("_Surface", 1f); // 1 = Transparent
                material.SetFloat("_Blend", 0f);    // 0 = Alpha
                material.SetFloat("_ZWrite", 0f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.SetColor("_BaseColor", transparent);
            }
            else
            {
                material.SetFloat("_Mode", 3f); // 3 = Transparent
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.color = transparent;
            }

            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            if (!AssetDatabase.IsValidFolder("Assets/Maps"))
            {
                Debug.LogWarning("[Friendslop] 'Assets/Maps' folder not found - creating the invisible spawn point material there anyway.");
            }

            AssetDatabase.CreateAsset(material, InvisibleMaterialPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[Friendslop] Created '{InvisibleMaterialPath}' for invisible player_spawn_point tiles.");
            return material;
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
        /// always correct, and the extra unused vertices are harmless for a collision-only mesh (PhysX
        /// collides against the triangle data, not the unused vertices sitting alongside it).
        ///
        /// The bounds are a different story. Mesh.RecalculateBounds() computes its AABB from every
        /// vertex in the mesh's vertex array, whether or not any triangle actually references it - so
        /// with the full original vertex buffer kept above, it would report bounds spanning the ENTIRE
        /// source mesh (e.g. the whole map) instead of just this submesh's own footprint (e.g. just the
        /// "start" tile). That's harmless for the MeshCollider itself, but CreateChallengeTrigger reads
        /// this mesh's bounds to size the challenge start/finish trigger volume around just this
        /// submesh - so we compute and assign the real, tight bounds explicitly instead, from only the
        /// vertices this submesh's triangles reference.
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
            subMesh.bounds = ComputeReferencedVertexBounds(subMesh.vertices, subMesh.triangles);
            return subMesh;
        }

        /// <summary>
        /// The bounds of only the vertices actually referenced by <paramref name="triangles"/> - unlike
        /// Mesh.RecalculateBounds(), which includes every vertex in <paramref name="vertices"/> whether
        /// or not any triangle uses it (see ExtractSubMesh above for why that matters here).
        /// </summary>
        private static Bounds ComputeReferencedVertexBounds(Vector3[] vertices, int[] triangles)
        {
            if (triangles.Length == 0 || vertices.Length == 0)
            {
                return new Bounds(vertices.Length > 0 ? vertices[0] : Vector3.zero, Vector3.zero);
            }

            Vector3 min = vertices[triangles[0]];
            Vector3 max = min;

            for (int i = 1; i < triangles.Length; i++)
            {
                Vector3 vertex = vertices[triangles[i]];
                min = Vector3.Min(min, vertex);
                max = Vector3.Max(max, vertex);
            }

            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            return bounds;
        }
    }
}
