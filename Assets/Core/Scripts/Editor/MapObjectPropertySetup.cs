using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Assigns a single <see cref="MapObjectPropertyType"/> to one or more whole selected objects, and
    /// builds/tears down whatever collider(s) and component(s) that property needs - the small-object
    /// counterpart to <see cref="MapColliderSetup"/> and <see cref="MapObstacleSetup"/>, which used to
    /// derive the same properties per-submesh from a single giant, multi-material combined map mesh (by
    /// material name).
    ///
    /// Why this exists: splitting one huge map mesh into a per-submesh collider for every ice patch,
    /// climbable wall, beam, rail, pole and spawn point on it was technically correct, but made that one
    /// map object (and the scene containing it) too large, memory-wise, to keep in source control once
    /// enough pieces existed side by side. The new approach is to author or import each piece as its own
    /// small, single-purpose object - the whole object IS a pole, or IS ice, etc. - and assemble several
    /// of those into their own small group in a mini scene, instead of one enormous combined mesh. This
    /// tool is what tags a single small object with exactly one property and builds the matching
    /// collider/behavior for it - the object's own placement, scale and orientation are left entirely as
    /// authored (see the per-property notes below for what each one assumes about orientation).
    ///
    /// The physics and behavior for each property are unchanged from the old per-submesh version - only
    /// how the property gets assigned (whole object vs. per-submesh material name) is different:
    /// - Regular: an ordinary flat MeshCollider on the object's own mesh, no PhysicMaterial.
    /// - Ice: the same flat MeshCollider, with the low-friction ice PhysicMaterial assigned.
    /// - Wall Climb: the same flat MeshCollider (still blocks the player normally) plus a
    ///   <see cref="ClimbableWall"/> marker - see WallClimbAbility.
    /// - Pole: no flat collider - a CapsuleCollider fitted to the object's own mesh bounds along LOCAL
    ///   +Y, plus a <see cref="Pole"/> component. Pole/PoleGrabAbility hard-assume local +Y is "along the
    ///   pole" (see Pole's own class summary), so the source object needs to actually be modeled/oriented
    ///   that way - this tool can't detect or correct that for you.
    /// - Balance Beam: no flat collider - a CapsuleCollider fitted along LOCAL +Z, plus a
    ///   <see cref="BalanceBeam"/> component. Same caveat as Pole, but for local +Z (see BalanceBeam's own
    ///   class summary). To chain several beam objects into one continuous run, hand-assign their Next In
    ///   Chain / Previous In Chain fields in the Inspector after tagging each one - exactly like before,
    ///   this tool doesn't need to do that automatically anymore since each beam is now its own object.
    /// - Grind Rail: no collider at all (rails were always collider-less - a proximity-snap mechanic, not
    ///   a physical surface). This tool DOES auto-detect the object's long axis from its mesh (unlike
    ///   Pole/Balance Beam) and buckets a waypoint chain along it, converted to world space, so a curved
    ///   rail object still produces a reasonably faithful path - see MapObstacleSetup's BuildRail, which
    ///   this mirrors.
    /// - Player Spawn Point: no collider, renderer hidden (its original material(s) are saved on the
    ///   object's MapObjectProperty and restored automatically if you switch it to any other property
    ///   later), plus a <see cref="PlayerSpawnPoint"/> marker - GameManager auto-discovers these at
    ///   runtime, no manual wiring needed. A mesh is optional for this one; it's fine to tag a bare marker
    ///   object.
    /// - Challenge Start / Challenge Finish: the same flat MeshCollider as Regular (still walkable floor)
    ///   PLUS an invisible BoxCollider trigger covering the object's full footprint extended through
    ///   ChallengeTriggerHeight of airspace (so jumping over it counts the same as walking through it),
    ///   wired up via a <see cref="ChallengeZoneTrigger"/>. The owning <see cref="ChallengeZone"/> is
    ///   found or created on this object's TOPMOST PARENT (transform.root) rather than the tagged object
    ///   itself, mirroring how MapColliderSetup put one ChallengeZone on the whole old combined-mesh
    ///   challenge piece - so build each challenge's start/finish/ice/beam/etc. pieces as children under
    ///   one common parent in your mini scene, the same way you'd have grouped them before, and they'll
    ///   share a single ChallengeZone (with its own auto-created ChallengeDefinition asset, reused on
    ///   later runs - your edits to its name/points/pars are never overwritten).
    ///
    /// Usage: select one or more objects in the Hierarchy (each needs a MeshFilter + MeshRenderer, except
    /// Player Spawn Point where a mesh is optional) and run Friendslop > Set Object Property > <property>.
    /// Multi-selection is supported, so you can tag a batch of identical pieces (e.g. several ice patches)
    /// in one go. Safe to re-run, including switching an object to a different property later - any
    /// collider(s) and marker component(s) from a previous run of this tool are removed and rebuilt from
    /// scratch each time, and a hidden Player Spawn Point renderer's original material is restored first
    /// if you switch it away from that property.
    /// </summary>
    public static class MapObjectPropertySetup
    {
        private const string IceKeyword = "ice";
        private const string IcePhysicMaterialPath = "Assets/Maps/PM_Ice.physicMaterial";

        // Keep in sync with MapColliderSetup's own copy of this path - both tools create/reuse the same asset.
        private const string InvisibleMaterialPath = "Assets/Maps/M_SpawnPointInvisible.mat";

        // Keep in sync with MapColliderSetup's own copy of these.
        private const string ChallengeDefinitionFolder = "Assets/Core/Data/Challenges";
        private const float ChallengeTriggerHeight = 4f;

        private const float MinCrossSectionRadius = 0.1f;
        private const float RailWaypointSpacing = 2f;

        private const string UndoName = "Set Object Property";

        #region Menu Items

        [MenuItem("Friendslop/Set Object Property/Regular")]
        private static void SetRegular() => ApplyToSelection(MapObjectPropertyType.Regular);

        [MenuItem("Friendslop/Set Object Property/Ice")]
        private static void SetIce() => ApplyToSelection(MapObjectPropertyType.Ice);

        [MenuItem("Friendslop/Set Object Property/Wall Climb")]
        private static void SetWallClimb() => ApplyToSelection(MapObjectPropertyType.WallClimb);

        [MenuItem("Friendslop/Set Object Property/Pole")]
        private static void SetPole() => ApplyToSelection(MapObjectPropertyType.Pole);

        [MenuItem("Friendslop/Set Object Property/Balance Beam")]
        private static void SetBalanceBeam() => ApplyToSelection(MapObjectPropertyType.BalanceBeam);

        [MenuItem("Friendslop/Set Object Property/Grind Rail")]
        private static void SetGrindRail() => ApplyToSelection(MapObjectPropertyType.GrindRail);

        [MenuItem("Friendslop/Set Object Property/Player Spawn Point")]
        private static void SetPlayerSpawnPoint() => ApplyToSelection(MapObjectPropertyType.PlayerSpawnPoint);

        [MenuItem("Friendslop/Set Object Property/Challenge Start")]
        private static void SetChallengeStart() => ApplyToSelection(MapObjectPropertyType.ChallengeStart);

        [MenuItem("Friendslop/Set Object Property/Challenge Finish")]
        private static void SetChallengeFinish() => ApplyToSelection(MapObjectPropertyType.ChallengeFinish);

        #endregion

        private static void ApplyToSelection(MapObjectPropertyType type)
        {
            GameObject[] selection = Selection.gameObjects;
            if (selection == null || selection.Length == 0)
            {
                Debug.LogError("[Friendslop] Select one or more objects in the Hierarchy first.");
                return;
            }

            Undo.SetCurrentGroupName(UndoName);
            int undoGroup = Undo.GetCurrentGroup();

            int applied = 0;
            foreach (GameObject obj in selection)
            {
                if (ApplyProperty(obj, type))
                {
                    applied++;
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log($"[Friendslop] Set {applied}/{selection.Length} selected object(s) to '{type}'.");
        }

        /// <summary>
        /// The actual per-object logic, callable directly against a known GameObject rather than only via
        /// the current Hierarchy selection. Returns false (after logging why) if the object couldn't be
        /// configured for the requested property.
        /// </summary>
        public static bool ApplyProperty(GameObject obj, MapObjectPropertyType type)
        {
            if (obj == null) return false;

            obj.TryGetComponent(out MeshFilter meshFilter);
            obj.TryGetComponent(out MeshRenderer meshRenderer);
            bool hasMesh = meshFilter != null && meshFilter.sharedMesh != null;

            if (type != MapObjectPropertyType.PlayerSpawnPoint && !hasMesh)
            {
                Debug.LogError($"[Friendslop] '{obj.name}' needs a MeshFilter (with a mesh assigned) to be set to '{type}'.");
                return false;
            }

            MapObjectProperty tag = GetOrAddTag(obj);
            TearDown(obj, tag);

            switch (type)
            {
                case MapObjectPropertyType.Regular:
                    BuildFlatCollider(obj, meshFilter, null);
                    break;

                case MapObjectPropertyType.Ice:
                    var icePhysicMaterial = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(IcePhysicMaterialPath);
                    if (icePhysicMaterial == null)
                    {
                        Debug.LogError($"[Friendslop] Couldn't find the ice PhysicMaterial at '{IcePhysicMaterialPath}'. Has it moved or been renamed?");
                        return false;
                    }
                    BuildFlatCollider(obj, meshFilter, icePhysicMaterial);
                    break;

                case MapObjectPropertyType.WallClimb:
                    BuildFlatCollider(obj, meshFilter, null);
                    Undo.AddComponent<ClimbableWall>(obj);
                    break;

                case MapObjectPropertyType.Pole:
                    BuildFittedCapsule(obj, meshFilter, axis: 1); // Y - matches Pole's "local +Y = along the pole".
                    Undo.AddComponent<Pole>(obj);
                    break;

                case MapObjectPropertyType.BalanceBeam:
                    BuildFittedCapsule(obj, meshFilter, axis: 2); // Z - matches BalanceBeam's "local +Z = along the beam".
                    Undo.AddComponent<BalanceBeam>(obj);
                    break;

                case MapObjectPropertyType.GrindRail:
                    BuildGrindRail(obj, meshFilter);
                    break;

                case MapObjectPropertyType.PlayerSpawnPoint:
                    HidePlayerSpawnPointRenderer(obj, meshRenderer, tag);
                    Undo.AddComponent<PlayerSpawnPoint>(obj);
                    break;

                case MapObjectPropertyType.ChallengeStart:
                    BuildFlatCollider(obj, meshFilter, null);
                    BuildChallengeTrigger(obj, meshFilter, ChallengeZoneKind.Start);
                    break;

                case MapObjectPropertyType.ChallengeFinish:
                    BuildFlatCollider(obj, meshFilter, null);
                    BuildChallengeTrigger(obj, meshFilter, ChallengeZoneKind.Finish);
                    break;
            }

            tag.PropertyType = type;
            EditorUtility.SetDirty(obj);
            return true;
        }

        #region Teardown

        private static MapObjectProperty GetOrAddTag(GameObject obj)
        {
            if (!obj.TryGetComponent(out MapObjectProperty tag))
            {
                tag = Undo.AddComponent<MapObjectProperty>(obj);
            }
            return tag;
        }

        /// <summary>
        /// Removes anything a previous run of this tool could have added, so every ApplyProperty call
        /// starts from a clean slate - same "safe to re-run" pattern MapColliderSetup/MapObstacleSetup use.
        /// Restores a Player Spawn Point's hidden material first, if any, since the object might be about
        /// to become something other than a spawn point.
        /// </summary>
        private static void TearDown(GameObject obj, MapObjectProperty tag)
        {
            if (tag.SavedMaterialsBeforeHidden != null && tag.SavedMaterialsBeforeHidden.Length > 0
                && obj.TryGetComponent(out Renderer renderer))
            {
                renderer.sharedMaterials = tag.SavedMaterialsBeforeHidden;
            }
            tag.SavedMaterialsBeforeHidden = null;

            RemoveComponent<ClimbableWall>(obj);
            RemoveComponent<Pole>(obj);
            RemoveComponent<BalanceBeam>(obj);
            RemoveComponent<GrindRail>(obj);
            RemoveComponent<PlayerSpawnPoint>(obj);
            RemoveComponent<ChallengeZoneTrigger>(obj);

            foreach (Collider collider in obj.GetComponents<Collider>())
            {
                Undo.DestroyObjectImmediate(collider);
            }
        }

        private static void RemoveComponent<T>(GameObject obj) where T : Component
        {
            if (obj.TryGetComponent(out T component))
            {
                Undo.DestroyObjectImmediate(component);
            }
        }

        #endregion

        #region Builders

        /// <summary>Adds an ordinary flat MeshCollider using the object's own mesh, with the given PhysicMaterial (or none).</summary>
        private static void BuildFlatCollider(GameObject obj, MeshFilter meshFilter, PhysicsMaterial physicMaterial)
        {
            var collider = Undo.AddComponent<MeshCollider>(obj);
            collider.sharedMesh = meshFilter.sharedMesh;
            collider.sharedMaterial = physicMaterial;
        }

        /// <summary>
        /// Fits a CapsuleCollider to the object's own mesh local-space bounds along the given fixed local
        /// axis (0=X, 1=Y, 2=Z) - NOT auto-detected, since Pole and BalanceBeam both hard-assume a
        /// specific local axis is "along" them (see their own class summaries) regardless of what shape
        /// their collider actually is. If the source object isn't modeled/oriented to match, fix the
        /// object's orientation (or its mesh) rather than this tool - it can't tell which way you meant
        /// "along" to point.
        /// </summary>
        private static void BuildFittedCapsule(GameObject obj, MeshFilter meshFilter, int axis)
        {
            Bounds bounds = meshFilter.sharedMesh.bounds;
            var capsule = Undo.AddComponent<CapsuleCollider>(obj);
            capsule.direction = axis;
            capsule.center = bounds.center;

            float height = axis == 0 ? bounds.size.x : axis == 1 ? bounds.size.y : bounds.size.z;
            float radius = axis == 0
                ? Mathf.Max(bounds.extents.y, bounds.extents.z)
                : axis == 1
                    ? Mathf.Max(bounds.extents.x, bounds.extents.z)
                    : Mathf.Max(bounds.extents.x, bounds.extents.y);

            capsule.height = Mathf.Max(height, MinCrossSectionRadius * 2f);
            capsule.radius = Mathf.Max(radius, MinCrossSectionRadius);
        }

        /// <summary>
        /// Builds a (collider-less) GrindRail from the object's own mesh: unlike Pole/BalanceBeam, the
        /// rail's long axis IS auto-detected from the mesh's own vertices (the two most distant points),
        /// then bucketed into a waypoint chain along that axis the same way MapObstacleSetup.BuildRail
        /// does, so a curved rail object still produces a reasonably faithful path rather than collapsing
        /// to a single straight line between its two ends. GrindRail stores world-space waypoints and has
        /// no fixed local-axis requirement of its own, which is what makes auto-detection safe here in a
        /// way it isn't for Pole/BalanceBeam.
        /// </summary>
        private static void BuildGrindRail(GameObject obj, MeshFilter meshFilter)
        {
            Vector3[] localVerts = meshFilter.sharedMesh.vertices;
            var points = new List<Vector3>(localVerts);

            FindLongAxis(points, out Vector3 extremeA, out Vector3 extremeB, out float length);
            Vector3 axis = length > 0.0001f ? (extremeB - extremeA) / length : Vector3.forward;

            List<Vector3> waypointsLocal = BucketAlongAxis(points, extremeA, axis, length);
            if (waypointsLocal.Count < 2)
            {
                waypointsLocal = new List<Vector3> { extremeA, extremeB };
            }

            float radius = Mathf.Max(MaxDistanceToPolyline(points, waypointsLocal), MinCrossSectionRadius);

            var waypointsWorld = new Vector3[waypointsLocal.Count];
            for (int i = 0; i < waypointsLocal.Count; i++)
            {
                waypointsWorld[i] = obj.transform.TransformPoint(waypointsLocal[i]);
            }

            GrindRail rail = Undo.AddComponent<GrindRail>(obj);
            rail.Configure(waypointsWorld, radius);
        }

        /// <summary>
        /// Saves the renderer's current material(s) onto the object's MapObjectProperty (so switching to
        /// any other property later restores them - see TearDown) and swaps in the shared invisible
        /// spawn-point material. A mesh/renderer is optional for a spawn point, so this is a no-op if
        /// there's no Renderer to hide.
        /// </summary>
        private static void HidePlayerSpawnPointRenderer(GameObject obj, Renderer renderer, MapObjectProperty tag)
        {
            if (renderer == null) return;

            Material invisibleMaterial = EnsureInvisibleMaterial();
            if (invisibleMaterial == null) return;

            tag.SavedMaterialsBeforeHidden = renderer.sharedMaterials;

            var hiddenMaterials = new Material[renderer.sharedMaterials.Length];
            for (int i = 0; i < hiddenMaterials.Length; i++)
            {
                hiddenMaterials[i] = invisibleMaterial;
            }
            renderer.sharedMaterials = hiddenMaterials;
        }

        /// <summary>
        /// Adds the invisible full-height trigger volume and wires it to this object's (or created-on-
        /// the-fly) shared ChallengeZone - see the class summary for why the zone lives on
        /// transform.root rather than the tagged object itself.
        /// </summary>
        private static void BuildChallengeTrigger(GameObject obj, MeshFilter meshFilter, ChallengeZoneKind kind)
        {
            Bounds bounds = meshFilter.sharedMesh.bounds;

            var box = Undo.AddComponent<BoxCollider>(obj);
            box.isTrigger = true;
            box.center = bounds.center + new Vector3(0f, ChallengeTriggerHeight * 0.5f, 0f);
            box.size = new Vector3(bounds.size.x, ChallengeTriggerHeight, bounds.size.z);

            ChallengeZone zone = EnsureChallengeZone(obj.transform.root.gameObject);
            if (zone == null) return;

            ChallengeZoneTrigger trigger = Undo.AddComponent<ChallengeZoneTrigger>(obj);
            trigger.Configure(zone, kind);
        }

        #endregion

        #region Shared Assets (mirrors MapColliderSetup's own copies)

        /// <summary>
        /// Gets or adds a ChallengeZone on the given root object, creating it a brand new
        /// ChallengeDefinition asset (named after the root object, with the class defaults) the first
        /// time this runs against that root - re-running later (from any start/finish object sharing the
        /// same root) reuses both. Mirrors MapColliderSetup's own EnsureChallengeZone.
        /// </summary>
        private static ChallengeZone EnsureChallengeZone(GameObject root)
        {
            if (!root.TryGetComponent(out ChallengeZone zone))
            {
                zone = Undo.AddComponent<ChallengeZone>(root);
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
                definition.challengeName = root.name;

                string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{ChallengeDefinitionFolder}/{root.name}Definition.asset");
                AssetDatabase.CreateAsset(definition, assetPath);
                AssetDatabase.SaveAssets();

                definitionProperty.objectReferenceValue = definition;
                serializedZone.ApplyModifiedProperties();

                Debug.Log($"[Friendslop] Created '{assetPath}' for '{root.name}' - edit its name/points/pars there.");
            }

            return zone;
        }

        /// <summary>
        /// Gets or creates the same fully transparent Material MapColliderSetup uses to hide
        /// "player_spawn_point" tiles - reuses the identical asset (by path) so both tools' spawn points
        /// look the same and don't create duplicate materials.
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
                Debug.LogError("[Friendslop] Couldn't find a URP Lit or Standard shader to build the invisible spawn point material - Player Spawn Point objects will keep their current material.");
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

            Debug.Log($"[Friendslop] Created '{InvisibleMaterialPath}' for invisible Player Spawn Point objects.");
            return material;
        }

        #endregion

        #region Geometry Helpers (mirrors MapObstacleSetup's own copies, operating on a whole mesh's vertices instead of one submesh's triangle-connected component)

        /// <summary>Finds the two most-distant points in the set - defines the mesh's long axis and length.</summary>
        private static void FindLongAxis(List<Vector3> points, out Vector3 extremeA, out Vector3 extremeB, out float length)
        {
            extremeA = points[0];
            extremeB = points[0];
            float bestDistSqr = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                for (int j = i + 1; j < points.Count; j++)
                {
                    float d = (points[i] - points[j]).sqrMagnitude;
                    if (d > bestDistSqr)
                    {
                        bestDistSqr = d;
                        extremeA = points[i];
                        extremeB = points[j];
                    }
                }
            }
            length = Mathf.Sqrt(bestDistSqr);
        }

        /// <summary>
        /// Buckets points along the axis (roughly one bucket every RailWaypointSpacing units) and
        /// averages each non-empty bucket into a waypoint, in order from `origin` outward.
        /// </summary>
        private static List<Vector3> BucketAlongAxis(List<Vector3> points, Vector3 origin, Vector3 axis, float length)
        {
            int bucketCount = Mathf.Max(2, Mathf.RoundToInt(length / RailWaypointSpacing) + 1);
            var sums = new Vector3[bucketCount];
            var counts = new int[bucketCount];

            foreach (Vector3 p in points)
            {
                float t = length > 0.0001f ? Mathf.Clamp01(Vector3.Dot(p - origin, axis) / length) : 0f;
                int bucket = Mathf.Clamp(Mathf.RoundToInt(t * (bucketCount - 1)), 0, bucketCount - 1);
                sums[bucket] += p;
                counts[bucket]++;
            }

            var waypoints = new List<Vector3>(bucketCount);
            for (int i = 0; i < bucketCount; i++)
            {
                if (counts[i] > 0)
                {
                    waypoints.Add(sums[i] / counts[i]);
                }
            }
            return waypoints;
        }

        /// <summary>For every point, finds the closest point on the given polyline and returns the largest such perpendicular distance seen across all points - the mesh's effective cross-section radius along a (possibly bent) path.</summary>
        private static float MaxDistanceToPolyline(List<Vector3> points, List<Vector3> polyline)
        {
            if (polyline.Count < 2) return 0f;

            float maxDistSqr = 0f;
            foreach (Vector3 p in points)
            {
                float closestSqr = float.MaxValue;
                for (int i = 0; i < polyline.Count - 1; i++)
                {
                    Vector3 a = polyline[i];
                    Vector3 b = polyline[i + 1];
                    Vector3 ab = b - a;
                    float lenSqr = ab.sqrMagnitude;
                    float t = lenSqr > 0.0001f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / lenSqr) : 0f;
                    Vector3 closest = a + ab * t;
                    float distSqr = (p - closest).sqrMagnitude;
                    if (distSqr < closestSqr) closestSqr = distSqr;
                }
                if (closestSqr > maxDistSqr) maxDistSqr = closestSqr;
            }
            return Mathf.Sqrt(maxDistSqr);
        }

        #endregion
    }
}
