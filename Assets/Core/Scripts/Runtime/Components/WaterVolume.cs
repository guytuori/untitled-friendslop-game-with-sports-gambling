using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Water (Set Object Property > Water): touching it kills you, like falling onto the death plane - a
    /// normal death, and you come back on the safe ground nearest to where you went in (LocalRespawnTracker).
    ///
    /// It has no collider, so nothing stands on it or bumps into it. Instead the object's own mesh is used as
    /// a volume: a point is in the water if, looking straight up from it, the first surface of the mesh it
    /// meets is a top (upward-facing) one - so blocks of water of any shape, stacked or not, all work, and
    /// you're safe underneath water (in a tunnel below a pond). You die once your feet are
    /// <see cref="DeathDepth"/> into the water, so skimming the surface is fine.
    ///
    /// Works in the object's own space, so water that moves (rises, say) still works.
    /// </summary>
    [DisallowMultipleComponent]
    public class WaterVolume : MonoBehaviour
    {
        /// <summary>How far (meters) your feet can go into the water before it kills you.</summary>
        public const float DeathDepth = 0.15f;

        private const float CellSize = 2f;

        private static readonly List<WaterVolume> s_All = new List<WaterVolume>();

        /// <summary>Every enabled water object.</summary>
        public static IReadOnlyList<WaterVolume> All => s_All;

        private struct Face
        {
            public Vector3 A, B, C;
            public bool Up;
        }

        private readonly List<Face> m_Faces = new List<Face>();
        private readonly Dictionary<long, List<int>> m_Cells = new Dictionary<long, List<int>>();
        private Bounds m_LocalBounds;
        private bool m_Built;
        private bool m_UseBoxOnly;

        private void Awake()
        {
            // Water is never solid (Set Object Property doesn't give it a collider; this catches one added by hand).
            foreach (Collider collider in GetComponents<Collider>()) Destroy(collider);
            Build();
        }

        private void OnEnable()
        {
            if (!s_All.Contains(this)) s_All.Add(this);
        }

        private void OnDisable()
        {
            s_All.Remove(this);
        }

        /// <summary>True if the point is inside any water.</summary>
        public static bool IsInWater(Vector3 worldPoint)
        {
            for (int i = 0; i < s_All.Count; i++)
            {
                if (s_All[i] != null && s_All[i].Contains(worldPoint)) return true;
            }
            return false;
        }

        /// <summary>True if the point is inside this water.</summary>
        public bool Contains(Vector3 worldPoint)
        {
            if (!m_Built) return false;

            Vector3 p = transform.InverseTransformPoint(worldPoint);
            if (p.x < m_LocalBounds.min.x || p.x > m_LocalBounds.max.x ||
                p.z < m_LocalBounds.min.z || p.z > m_LocalBounds.max.z ||
                p.y < m_LocalBounds.min.y || p.y > m_LocalBounds.max.y)
            {
                return false;
            }
            if (m_UseBoxOnly) return true;

            if (!m_Cells.TryGetValue(CellKey(p.x, p.z), out List<int> faces)) return false;

            // The nearest surface straight above the point: a top means we're in the water, a bottom means
            // we're underneath it.
            float nearest = float.MaxValue;
            bool nearestIsTop = false;
            foreach (int index in faces)
            {
                Face face = m_Faces[index];
                if (!TryHeightAt(face, p.x, p.z, out float height) || height < p.y) continue;
                if (height < nearest)
                {
                    nearest = height;
                    nearestIsTop = face.Up;
                }
            }
            return nearest < float.MaxValue && nearestIsTop;
        }

        private void Build()
        {
            m_Faces.Clear();
            m_Cells.Clear();
            m_Built = false;

            if (!TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null)
            {
                Debug.LogWarning($"[Water] '{name}' has no mesh, so it can't be water.", this);
                return;
            }

            Mesh mesh = filter.sharedMesh;
            m_LocalBounds = mesh.bounds;
            m_LocalBounds.Expand(new Vector3(0.01f, 0.01f, 0.01f));

            if (!mesh.isReadable)
            {
                // Can't read the shape - treat the whole box as water (turn on Read/Write in the glb's import settings).
                Debug.LogWarning($"[Water] '{name}''s mesh isn't readable (Read/Write off in its import settings), so its whole bounding box counts as water.", this);
                m_UseBoxOnly = true;
                m_Built = true;
                return;
            }

            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            bool hasNormals = normals != null && normals.Length == vertices.Length;
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                int[] triangles = mesh.GetTriangles(sub);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    Vector3 a = vertices[triangles[t]];
                    Vector3 b = vertices[triangles[t + 1]];
                    Vector3 c = vertices[triangles[t + 2]];

                    Vector3 winding = Vector3.Cross(b - a, c - a);
                    if (winding.sqrMagnitude < 1e-12f) continue;

                    // Which way the face points: the imported normals if there are any (they survive the
                    // import's axis mirroring), otherwise the winding.
                    Vector3 normal = hasNormals
                        ? normals[triangles[t]] + normals[triangles[t + 1]] + normals[triangles[t + 2]]
                        : winding;
                    float length = normal.magnitude;
                    if (length < 1e-6f) continue;
                    float up = normal.y / length;
                    if (Mathf.Abs(up) < 0.1f) continue; // a side - doesn't matter for "what's above me"

                    m_Faces.Add(new Face { A = a, B = b, C = c, Up = up > 0f });
                }
            }

            for (int i = 0; i < m_Faces.Count; i++)
            {
                Face face = m_Faces[i];
                float minX = Mathf.Min(face.A.x, Mathf.Min(face.B.x, face.C.x));
                float maxX = Mathf.Max(face.A.x, Mathf.Max(face.B.x, face.C.x));
                float minZ = Mathf.Min(face.A.z, Mathf.Min(face.B.z, face.C.z));
                float maxZ = Mathf.Max(face.A.z, Mathf.Max(face.B.z, face.C.z));
                for (int cx = CellIndex(minX); cx <= CellIndex(maxX); cx++)
                {
                    for (int cz = CellIndex(minZ); cz <= CellIndex(maxZ); cz++)
                    {
                        long key = ((long)cx << 32) ^ (uint)cz;
                        if (!m_Cells.TryGetValue(key, out List<int> list)) m_Cells[key] = list = new List<int>();
                        list.Add(i);
                    }
                }
            }

            m_Built = true;
        }

        private static int CellIndex(float v) => Mathf.FloorToInt(v / CellSize);

        private static long CellKey(float x, float z) => ((long)CellIndex(x) << 32) ^ (uint)CellIndex(z);

        /// <summary>The face's height at (x, z), if (x, z) is over it (seen from above, edges included).</summary>
        private static bool TryHeightAt(Face face, float x, float z, out float height)
        {
            height = 0f;
            float d = (face.B.z - face.C.z) * (face.A.x - face.C.x) + (face.C.x - face.B.x) * (face.A.z - face.C.z);
            if (Mathf.Abs(d) < 1e-8f) return false;

            float u = ((face.B.z - face.C.z) * (x - face.C.x) + (face.C.x - face.B.x) * (z - face.C.z)) / d;
            float v = ((face.C.z - face.A.z) * (x - face.C.x) + (face.A.x - face.C.x) * (z - face.C.z)) / d;
            float w = 1f - u - v;
            const float e = -1e-4f;
            if (u < e || v < e || w < e) return false;

            height = u * face.A.y + v * face.B.y + w * face.C.y;
            return true;
        }
    }
}
