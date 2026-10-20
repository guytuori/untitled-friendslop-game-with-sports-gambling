using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One placed challenge, as a race course: a start flag to run through and an end flag to reach. Added at
    /// runtime by MatchLayout to every challenge it places (not the Practice Map's plain pieces) - no prefab
    /// setup needed.
    ///
    /// THE FLAGS: Challenge16StartFlag / Challenge16EndFlag (or the 32 ones), from the ChallengeCatalog, are
    /// placed over the challenge's start row (its challengestart entities) and finish row (challengefinish),
    /// standing on the row's floor and as wide as the challenge. They're drawn see-through (the catalog's flag
    /// material, 33% opaque) and have no collision. What each player sees is their own:
    ///   - not done yet: the start flag;
    ///   - in it: the end flag (the start flag is gone);
    ///   - done: neither.
    /// Leaving without finishing (out the side, back out the start, or dying) brings the start flag back.
    ///
    /// THE RULES (checked by <see cref="LocalChallengeTracker"/> on each player's own machine):
    ///   - a run only starts by passing through the start flag from outside, so walking in from the side
    ///     doesn't count;
    ///   - it's completed by passing through the end flag;
    ///   - it's abandoned by leaving the challenge's square (<see cref="Contains"/>) any other way, or dying.
    /// <see cref="ChallengeManager"/> (master client) awards the points: every player can complete every
    /// challenge once per round, and any number of players can be in the same challenge at once.
    /// </summary>
    public class ChallengeCourse : MonoBehaviour
    {
        /// <summary>This machine's player's progress on this challenge.</summary>
        public enum LocalState
        {
            /// <summary>Not done yet: the start flag shows.</summary>
            Open,
            /// <summary>In it now: the end flag shows.</summary>
            Running,
            /// <summary>Completed this round: no flags.</summary>
            Done,
        }

        private const float Cell = 2f;             // Total Editor cell size: entities sit at cell centres
        private const float GateSideMargin = 0.3f;  // how far past a flag's side edge a crossing still counts
        private const float GateBelowMargin = 1.5f; // feet a little under the flag's bottom edge still count

        private static readonly List<ChallengeCourse> s_All = new List<ChallengeCourse>();

        /// <summary>Every challenge course in the loaded scenes.</summary>
        public static IReadOnlyList<ChallengeCourse> All => s_All;

        /// <summary>16 or 32.</summary>
        public int Size { get; private set; }

        /// <summary>The challenge's name and points (from its start's ChallengeZone). May be null.</summary>
        public ChallengeDefinition Definition { get; private set; }

        /// <summary>Points for completing it.</summary>
        public int Points => Definition != null ? Mathf.Max(0, Definition.basePoints) : 0;

        /// <summary>This course's index in ChallengeManager's list (the same on every client), or -1.</summary>
        public int Index { get; internal set; } = -1;

        /// <summary>This machine's player's progress (drives which flag shows).</summary>
        public LocalState State => m_State;

        private LocalState m_State;
        private float m_MinX, m_MaxX, m_MinZ, m_MaxZ; // the challenge's square, in this object's space
        private Gate m_StartGate;
        private Gate m_EndGate;

        private static readonly Dictionary<Texture, Material> s_FlagMaterials = new Dictionary<Texture, Material>();
        private static Material s_FlagMaterialSource;

        private void OnEnable()
        {
            if (!s_All.Contains(this)) s_All.Add(this);
        }

        private void OnDisable()
        {
            s_All.Remove(this);
        }

        #region Set-up

        /// <summary>
        /// Makes <paramref name="challengeRoot"/> (a placed challenge prefab) a course: works out its square from
        /// its start / finish rows, places the flags and switches off the old start / finish trigger volumes.
        /// Returns null (with a warning) if the challenge has no start or finish row.
        /// </summary>
        public static ChallengeCourse SetUp(GameObject challengeRoot, int size, ChallengeCatalog catalog)
        {
            if (challengeRoot == null) return null;
            Transform root = challengeRoot.transform;

            // The rows, before any flag (whose own glb carries challengestart entities too) is added.
            var starts = new List<Vector3>();
            var finishes = new List<Vector3>();
            foreach (Transform t in challengeRoot.GetComponentsInChildren<Transform>(true))
            {
                string name = t.name.ToLowerInvariant();
                if (name.StartsWith("challengestart")) starts.Add(root.InverseTransformPoint(t.position));
                else if (name.StartsWith("challengefinish")) finishes.Add(root.InverseTransformPoint(t.position));
            }
            if (starts.Count == 0 || finishes.Count == 0)
            {
                Debug.LogWarning($"[ChallengeCourse] '{challengeRoot.name}' has no {(starts.Count == 0 ? "challengestart" : "challengefinish")} entities, so it can't be run as a challenge.", challengeRoot);
                return null;
            }

            var course = challengeRoot.GetComponent<ChallengeCourse>();
            if (course == null) course = challengeRoot.AddComponent<ChallengeCourse>();
            course.Size = size;
            course.Definition = FindDefinition(challengeRoot);

            // The square: every start / finish cell (entities are at cell centres, so half a cell out).
            course.m_MinX = course.m_MinZ = float.MaxValue;
            course.m_MaxX = course.m_MaxZ = float.MinValue;
            foreach (Vector3 p in starts) course.Grow(p);
            foreach (Vector3 p in finishes) course.Grow(p);
            course.m_MinX -= Cell * 0.5f;
            course.m_MaxX += Cell * 0.5f;
            course.m_MinZ -= Cell * 0.5f;
            course.m_MaxZ += Cell * 0.5f;

            // The old trigger volumes started a challenge from any side - the flags do it now.
            foreach (ChallengeZoneTrigger trigger in challengeRoot.GetComponentsInChildren<ChallengeZoneTrigger>(true))
            {
                foreach (Collider collider in trigger.GetComponents<Collider>())
                {
                    if (collider.isTrigger) collider.enabled = false;
                }
            }

            GameObject startPrefab = null, endPrefab = null;
            if (catalog != null) catalog.GetFlags(size, out startPrefab, out endPrefab);
            Material flagMaterial = catalog != null ? catalog.FlagMaterial : null;
            if (startPrefab == null || endPrefab == null)
            {
                Debug.LogWarning($"[ChallengeCourse] No Challenge{size}StartFlag / Challenge{size}EndFlag in the ChallengeCatalog - '{challengeRoot.name}' gets an invisible start and end line instead. Rebuild the lists (Friendslop > Maps > Rebuild Map And Challenge Lists).", challengeRoot);
            }

            Vector3 centre = new Vector3((course.m_MinX + course.m_MaxX) * 0.5f, 0f, (course.m_MinZ + course.m_MaxZ) * 0.5f);
            course.m_StartGate = course.MakeGate("Start Flag", startPrefab, starts, flagMaterial, centre);
            course.m_EndGate = course.MakeGate("End Flag", endPrefab, finishes, flagMaterial, centre);

            course.SetLocalState(LocalState.Open);
            return course;
        }

        private void Grow(Vector3 p)
        {
            m_MinX = Mathf.Min(m_MinX, p.x);
            m_MaxX = Mathf.Max(m_MaxX, p.x);
            m_MinZ = Mathf.Min(m_MinZ, p.z);
            m_MaxZ = Mathf.Max(m_MaxZ, p.z);
        }

        /// <summary>The start's ChallengeZone's definition (the one its Start trigger points at), else any.</summary>
        private static ChallengeDefinition FindDefinition(GameObject challengeRoot)
        {
            foreach (ChallengeZoneTrigger trigger in challengeRoot.GetComponentsInChildren<ChallengeZoneTrigger>(true))
            {
                if (trigger.Kind == ChallengeZoneKind.Start && trigger.Zone != null && trigger.Zone.Definition != null) return trigger.Zone.Definition;
            }
            foreach (ChallengeZone zone in challengeRoot.GetComponentsInChildren<ChallengeZone>(true))
            {
                if (zone.Definition != null) return zone.Definition;
            }
            Debug.LogWarning($"[ChallengeCourse] '{challengeRoot.name}' has no ChallengeDefinition - completing it is worth 0 points.", challengeRoot);
            return null;
        }

        /// <summary>
        /// Places a flag over a row: as wide as the square, centred on it, standing on the row's floor (entities
        /// are one cell up, at the cell's centre), turned to run along the row. Without a flag prefab, an
        /// invisible line of the same size is used, so the challenge still works.
        /// </summary>
        private Gate MakeGate(string objectName, GameObject prefab, List<Vector3> row, Material flagMaterial, Vector3 centre)
        {
            Vector3 rowMin = row[0], rowMax = row[0], rowMean = Vector3.zero;
            foreach (Vector3 p in row)
            {
                rowMin = Vector3.Min(rowMin, p);
                rowMax = Vector3.Max(rowMax, p);
                rowMean += p;
            }
            rowMean /= row.Count;
            bool alongX = rowMax.x - rowMin.x >= rowMax.z - rowMin.z;
            float floor = rowMean.y - Cell * 0.5f;

            GameObject flag;
            if (prefab != null)
            {
                flag = Instantiate(prefab, transform, false);
                flag.name = objectName;
                MakeGhost(flag, flagMaterial);
            }
            else
            {
                flag = new GameObject(objectName);
                flag.transform.SetParent(transform, false);
            }

            Transform t = flag.transform;
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;

            // Its box in this object's space (no mesh: a line 4 cells high and one cell deep, across the square).
            if (!TryGetLocalBounds(flag, transform, out Bounds bounds))
            {
                bounds = new Bounds(new Vector3(0f, Cell * 2f, 0f), new Vector3(alongX ? m_MaxX - m_MinX : Cell, Cell * 4f, alongX ? Cell : m_MaxZ - m_MinZ));
            }
            else if ((bounds.size.x >= bounds.size.z) != alongX)
            {
                // Turn it to run along the row.
                t.localRotation = Quaternion.Euler(0f, 90f, 0f);
                TryGetLocalBounds(flag, transform, out bounds);
            }

            // Centre it across the square, on the row's line, bottom on the row's floor.
            Vector3 target = alongX
                ? new Vector3(centre.x, floor, rowMean.z)
                : new Vector3(rowMean.x, floor, centre.z);
            Vector3 offset = target - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            t.localPosition += offset;
            bounds.center += offset;

            // The same box in the flag's own space, for the crossing maths.
            Bounds inFlag = default;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                Vector3 p = t.InverseTransformPoint(transform.TransformPoint(corner));
                if (i == 0) inFlag = new Bounds(p, Vector3.zero);
                else inFlag.Encapsulate(p);
            }
            return new Gate(t, inFlag, t.InverseTransformPoint(transform.TransformPoint(centre)));
        }

        /// <summary>No collision, no shadows, and (with the catalog's flag material) see-through.</summary>
        private static void MakeGhost(GameObject flag, Material flagMaterial)
        {
            foreach (Collider collider in flag.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
                Destroy(collider);
            }

            if (flagMaterial != null && flagMaterial != s_FlagMaterialSource)
            {
                // The material was changed (or this is a new play session in the editor): start the copies over.
                s_FlagMaterials.Clear();
                s_FlagMaterialSource = flagMaterial;
            }

            foreach (Renderer renderer in flag.GetComponentsInChildren<Renderer>(true))
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                if (flagMaterial == null) continue;

                Material[] materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    materials[i] = GhostOf(materials[i], flagMaterial);
                }
                renderer.sharedMaterials = materials;
            }
        }

        /// <summary>A copy of the flag material showing the same texture as <paramref name="original"/> (shared by every flag).</summary>
        private static Material GhostOf(Material original, Material flagMaterial)
        {
            Texture texture = MainTextureOf(original);
            if (texture == null) return flagMaterial;
            if (s_FlagMaterials.TryGetValue(texture, out Material ghost) && ghost != null) return ghost;

            ghost = new Material(flagMaterial) { name = flagMaterial.name + " (" + texture.name + ")" };
            if (ghost.HasProperty("_BaseMap")) ghost.SetTexture("_BaseMap", texture);
            if (ghost.HasProperty("_MainTex")) ghost.SetTexture("_MainTex", texture);
            s_FlagMaterials[texture] = ghost;
            return ghost;
        }

        private static Texture MainTextureOf(Material material)
        {
            if (material == null) return null;
            foreach (string name in new[] { "baseColorTexture", "_BaseMap", "_MainTex" })
            {
                if (material.HasProperty(name) && material.GetTexture(name) != null) return material.GetTexture(name);
            }
            foreach (int id in material.GetTexturePropertyNameIDs())
            {
                Texture texture = material.GetTexture(id);
                if (texture != null) return texture;
            }
            return null;
        }

        /// <summary>The meshes under <paramref name="obj"/>, as a box in <paramref name="space"/>'s space.</summary>
        private static bool TryGetLocalBounds(GameObject obj, Transform space, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (MeshFilter filter in obj.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                Bounds b = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 p = space.InverseTransformPoint(filter.transform.TransformPoint(corner));
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else bounds.Encapsulate(p);
                }
            }
            return any;
        }

        #endregion

        #region Rules (used by LocalChallengeTracker)

        /// <summary>Whether <paramref name="world"/> is over the challenge's square (widened by <paramref name="margin"/>), at any height.</summary>
        public bool Contains(Vector3 world, float margin)
        {
            Vector3 p = transform.InverseTransformPoint(world);
            return p.x >= m_MinX - margin && p.x <= m_MaxX + margin && p.z >= m_MinZ - margin && p.z <= m_MaxZ + margin;
        }

        /// <summary>Moving from <paramref name="from"/> to <paramref name="to"/> passed through the start flag, from outside in.</summary>
        public bool CrossesStart(Vector3 from, Vector3 to) => m_StartGate != null && m_StartGate.Crosses(from, to, inwardOnly: true);

        /// <summary>Moving from <paramref name="from"/> to <paramref name="to"/> passed through the end flag (either way).</summary>
        public bool CrossesEnd(Vector3 from, Vector3 to) => m_EndGate != null && m_EndGate.Crosses(from, to, inwardOnly: false);

        /// <summary>True for a spot outside the challenge, or on the outer side of its start flag - somewhere a run hasn't started yet.</summary>
        public bool IsBeforeStart(Vector3 world)
        {
            if (!Contains(world, 0f)) return true;
            return m_StartGate != null && m_StartGate.InwardDepth(world) < 0f;
        }

        /// <summary>Sets this machine's player's progress and shows the matching flag.</summary>
        public void SetLocalState(LocalState state)
        {
            m_State = state;
            if (m_StartGate != null) m_StartGate.SetVisible(state == LocalState.Open);
            if (m_EndGate != null) m_EndGate.SetVisible(state == LocalState.Running);
        }

        #endregion

        /// <summary>
        /// A flag as a line to cross: its middle plane (across its thin side), limited to its width and height.
        /// Worked out in the flag's own space, so a challenge turned in its hole turns its flags with it.
        /// </summary>
        private sealed class Gate
        {
            private readonly Transform m_Flag;
            private readonly Bounds m_Bounds; // in the flag's own space
            private readonly int m_Thin;      // 0 (x) or 2 (z): the axis the flag is thin along
            private readonly int m_Along;     // the other horizontal axis
            private readonly float m_InwardSign; // +1 if the challenge's middle is on the flag's +thin side

            /// <param name="boundsInFlag">The flag's box in its own space.</param>
            /// <param name="squareCentreInFlag">The middle of the challenge's square, in the flag's space (which side is "in").</param>
            public Gate(Transform flag, Bounds boundsInFlag, Vector3 squareCentreInFlag)
            {
                m_Flag = flag;
                m_Bounds = boundsInFlag;
                m_Thin = m_Bounds.size.x < m_Bounds.size.z ? 0 : 2;
                m_Along = m_Thin == 0 ? 2 : 0;
                m_InwardSign = squareCentreInFlag[m_Thin] >= m_Bounds.center[m_Thin] ? 1f : -1f;
            }

            /// <summary>How far past the flag's middle, towards the inside of the challenge, a point is (negative = outside).</summary>
            public float InwardDepth(Vector3 world)
            {
                Vector3 p = m_Flag.InverseTransformPoint(world);
                return (p[m_Thin] - m_Bounds.center[m_Thin]) * m_InwardSign;
            }

            public bool Crosses(Vector3 fromWorld, Vector3 toWorld, bool inwardOnly)
            {
                Vector3 a = m_Flag.InverseTransformPoint(fromWorld);
                Vector3 b = m_Flag.InverseTransformPoint(toWorld);
                float sa = (a[m_Thin] - m_Bounds.center[m_Thin]) * m_InwardSign;
                float sb = (b[m_Thin] - m_Bounds.center[m_Thin]) * m_InwardSign;

                bool inward = sa < 0f && sb >= 0f;
                bool outward = sa > 0f && sb <= 0f;
                if (!(inward || (!inwardOnly && outward))) return false;

                float t = Mathf.Approximately(sa, sb) ? 0f : sa / (sa - sb);
                Vector3 q = Vector3.Lerp(a, b, t);
                return q[m_Along] >= m_Bounds.min[m_Along] - GateSideMargin && q[m_Along] <= m_Bounds.max[m_Along] + GateSideMargin
                    && q.y >= m_Bounds.min.y - GateBelowMargin && q.y <= m_Bounds.max.y;
            }

            public void SetVisible(bool visible)
            {
                if (m_Flag != null && m_Flag.gameObject.activeSelf != visible) m_Flag.gameObject.SetActive(visible);
            }
        }
    }
}
