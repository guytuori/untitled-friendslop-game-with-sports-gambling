using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Turns any rail mesh exported from the map editor into a grind rail that works like Dan's segmented one
    /// (Assets/_prefabs/Grindrails/obstacle_grindrail_segmented.prefab), keeping the map editor's own look -
    /// used by Set Object Property > Grind Rail.
    ///
    /// 1. FINDING THE RAIL'S PATH. A rail mesh is made of thin bars: short edges go round each bar's
    ///    cross-section, long edges run along it. The edge lengths split into those two groups at their biggest
    ///    jump; vertices joined by short edges are one bar end, and the middle of each end is a point on the
    ///    rail's centre line (how far its vertices sit from that middle is the bar's thickness). Bars sharing an
    ///    end are joined, which gives a graph of the whole rail. Each run of it between dead ends / junctions
    ///    becomes one rail path; a ring (no ends at all) is a closed loop. Several separate rails in one mesh
    ///    (e.g. two rings) each become their own. If the edge lengths don't split (not a thin-bar mesh) the
    ///    caller falls back to one path along the mesh's long axis.
    /// 2. THE GRIND PATHS. One GrindRail per path, its waypoints in the object's own local space (so the rail
    ///    turns with a rotated challenge), along the top of the bar (the rider sits Dan's 0.15 m above that),
    ///    one about every meter like the ones Dan's GenerateWaypoints makes. A ring is ONE looping GrindRail
    ///    (closedLoop): you go round and round without ever reaching an end. (Dan's double ring is two
    ///    half-rails the player hops between instead; each hop restarts the grind at entry speed and holds you
    ///    at the joint for a moment, which made rings slow and sticky - the loop has no joint.)
    /// 3. DAN'S SEGMENTS, WITHOUT HIS LOOK. A "grindrailSegment" on the Grind layer with a capsule collider the
    ///    bar's own thickness for every straight run (split into pieces of up to 5 m like his) - what his rails
    ///    have for the surface raycast and collisions - but no cylinders or collars: the exported mesh stays as
    ///    the visible rail.
    ///
    /// Everything built lives under one "[Grind Rail]" child of the tagged object, which <see cref="Remove"/>
    /// deletes.
    /// </summary>
    public static class GrindRailBuilder
    {
        public const string ContainerName = "[Grind Rail]";

        // From Dan's obstacle_grindrail_segmented, in meters.
        public const float MaxSegmentLength = 5f;
        public const float WaypointSpacing = 1f;      // Dan's GenerateWaypoints steps 1 m
        public const float RiderOffset = 0.15f;       // GrindRail.radius: the rider sits this far above the path
        public const float DefaultBarRadius = 0.15f;  // when the bar's thickness can't be measured (fallback path)
        private const float MinBarRadius = 0.05f;
        private const float CollinearCos = 0.9998f; // ~1.1 degrees

        /// <summary>A run of the rail's centre line, in the mesh's local space.</summary>
        public sealed class RailPath
        {
            public List<Vector3> Points = new List<Vector3>();
            public bool Closed; // a loop: the last point joins back to the first (not repeated)
            public float Radius = DefaultBarRadius; // the bar's thickness (centre to outside), mesh units
        }

        // =====================================================================================
        // 1. The rail's path
        // =====================================================================================

        /// <summary>
        /// The centre lines of a thin-bar rail mesh, or null (with a reason) if the mesh isn't made of thin bars.
        /// </summary>
        public static List<RailPath> ExtractPaths(Mesh mesh, out string problem)
        {
            problem = null;
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            if (vertices.Length == 0 || triangles.Length == 0) { problem = "the mesh is empty"; return null; }

            // Weld split vertices (normals / UV seams) by position.
            var keyToIndex = new Dictionary<Vector3Int, int>();
            var welded = new List<Vector3>();
            var weldOf = new int[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = vertices[i];
                var key = new Vector3Int(Mathf.RoundToInt(v.x * 1000f), Mathf.RoundToInt(v.y * 1000f), Mathf.RoundToInt(v.z * 1000f));
                if (!keyToIndex.TryGetValue(key, out int index))
                {
                    index = welded.Count;
                    keyToIndex[key] = index;
                    welded.Add(v);
                }
                weldOf[i] = index;
            }

            // Unique edges and their lengths.
            var edgeSet = new HashSet<long>();
            var edges = new List<Vector2Int>();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    int a = weldOf[triangles[t + k]], b = weldOf[triangles[t + (k + 1) % 3]];
                    if (a == b) continue;
                    if (a > b) (a, b) = (b, a);
                    if (edgeSet.Add(((long)a << 32) | (uint)b)) edges.Add(new Vector2Int(a, b));
                }
            }
            var lengths = new float[edges.Count];
            for (int i = 0; i < edges.Count; i++) lengths[i] = Vector3.Distance(welded[edges[i].x], welded[edges[i].y]);

            // Short (round a bar) vs long (along a bar): the biggest jump between distinct lengths.
            var distinct = new SortedSet<float>();
            foreach (float l in lengths) if (l > 1e-4f) distinct.Add(Mathf.Round(l * 10000f) / 10000f);
            if (distinct.Count < 2) { problem = "all its edges are the same length"; return null; }
            var sorted = new List<float>(distinct);
            float bestRatio = 0f, threshold = 0f;
            for (int i = 1; i < sorted.Count; i++)
            {
                float ratio = sorted[i] / sorted[i - 1];
                if (ratio > bestRatio) { bestRatio = ratio; threshold = Mathf.Sqrt(sorted[i] * sorted[i - 1]); }
            }
            if (bestRatio < 2f) { problem = "it isn't made of thin bars (its edges don't split into short and long ones)"; return null; }

            // Bar ends: vertices joined by short edges.
            var ends = new UnionFind(welded.Count);
            for (int i = 0; i < edges.Count; i++) if (lengths[i] <= threshold) ends.Union(edges[i].x, edges[i].y);

            var endIndex = new Dictionary<int, int>();
            var endSum = new List<Vector3>();
            var endCount = new List<int>();
            var endOfVertex = new int[welded.Count];
            for (int v = 0; v < welded.Count; v++)
            {
                int root = ends.Find(v);
                if (!endIndex.TryGetValue(root, out int e))
                {
                    e = endSum.Count;
                    endIndex[root] = e;
                    endSum.Add(Vector3.zero);
                    endCount.Add(0);
                }
                endSum[e] += welded[v];
                endCount[e]++;
                endOfVertex[v] = e;
            }

            // Bar ends that touch (two bars meeting) are one point on the rail.
            var nodes = new UnionFind(endSum.Count);
            var centres = new Vector3[endSum.Count];
            for (int e = 0; e < centres.Length; e++) centres[e] = endSum[e] / endCount[e];

            // The bar's thickness: how far a bar end's vertices sit from its middle (the median over all ends).
            var endRadius = new float[centres.Length];
            for (int v = 0; v < welded.Count; v++)
            {
                int e = endOfVertex[v];
                endRadius[e] = Mathf.Max(endRadius[e], Vector3.Distance(welded[v], centres[e]));
            }
            var radii = new List<float>(endRadius);
            radii.Sort();
            float barRadius = radii.Count > 0 ? radii[radii.Count / 2] : DefaultBarRadius;
            for (int a = 0; a < centres.Length; a++)
            {
                for (int b = a + 1; b < centres.Length; b++)
                {
                    if ((centres[a] - centres[b]).sqrMagnitude <= threshold * threshold) nodes.Union(a, b);
                }
            }
            var nodeIndex = new Dictionary<int, int>();
            var nodeSum = new List<Vector3>();
            var nodeCount = new List<int>();
            var nodeOfEnd = new int[centres.Length];
            for (int e = 0; e < centres.Length; e++)
            {
                int root = nodes.Find(e);
                if (!nodeIndex.TryGetValue(root, out int n))
                {
                    n = nodeSum.Count;
                    nodeIndex[root] = n;
                    nodeSum.Add(Vector3.zero);
                    nodeCount.Add(0);
                }
                nodeSum[n] += centres[e];
                nodeCount[n]++;
                nodeOfEnd[e] = n;
            }
            var point = new Vector3[nodeSum.Count];
            for (int n = 0; n < point.Length; n++) point[n] = nodeSum[n] / nodeCount[n];

            // Bars: long edges between two different points.
            var neighbours = new List<HashSet<int>>();
            for (int n = 0; n < point.Length; n++) neighbours.Add(new HashSet<int>());
            for (int i = 0; i < edges.Count; i++)
            {
                if (lengths[i] <= threshold) continue;
                int a = nodeOfEnd[endOfVertex[edges[i].x]], b = nodeOfEnd[endOfVertex[edges[i].y]];
                if (a == b) continue;
                neighbours[a].Add(b);
                neighbours[b].Add(a);
            }

            List<RailPath> paths = WalkGraph(point, neighbours);
            if (paths.Count == 0) { problem = "no bars were found in it"; return null; }
            foreach (RailPath path in paths) path.Radius = barRadius;
            return paths;
        }

        /// <summary>Each run between dead ends / junctions, then whatever is left: rings.</summary>
        private static List<RailPath> WalkGraph(Vector3[] point, List<HashSet<int>> neighbours)
        {
            var paths = new List<RailPath>();
            var used = new HashSet<long>();
            long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

            for (int start = 0; start < point.Length; start++)
            {
                if (neighbours[start].Count == 2 || neighbours[start].Count == 0) continue;
                foreach (int first in neighbours[start])
                {
                    if (used.Contains(Key(start, first))) continue;
                    var nodes = new List<int> { start, first };
                    used.Add(Key(start, first));
                    int previous = start, current = first;
                    while (neighbours[current].Count == 2)
                    {
                        int next = -1;
                        foreach (int n in neighbours[current]) if (n != previous) next = n;
                        if (next < 0 || used.Contains(Key(current, next))) break;
                        used.Add(Key(current, next));
                        nodes.Add(next);
                        previous = current;
                        current = next;
                    }
                    paths.Add(ToPath(point, nodes));
                }
            }

            for (int start = 0; start < point.Length; start++)
            {
                foreach (int first in neighbours[start])
                {
                    if (used.Contains(Key(start, first))) continue;
                    var nodes = new List<int> { start, first };
                    used.Add(Key(start, first));
                    int previous = start, current = first;
                    while (current != start)
                    {
                        int next = -1;
                        foreach (int n in neighbours[current])
                        {
                            if (n != previous && !used.Contains(Key(current, n))) { next = n; break; }
                        }
                        if (next < 0) break;
                        used.Add(Key(current, next));
                        nodes.Add(next);
                        previous = current;
                        current = next;
                    }
                    paths.Add(ToPath(point, nodes));
                }
            }
            return paths;
        }

        private static RailPath ToPath(Vector3[] point, List<int> nodes)
        {
            var path = new RailPath();
            bool closed = nodes.Count > 3 && nodes[0] == nodes[nodes.Count - 1];
            int count = closed ? nodes.Count - 1 : nodes.Count;
            for (int i = 0; i < count; i++) path.Points.Add(point[nodes[i]]);
            path.Closed = closed;
            return path;
        }

        // =====================================================================================
        // 2 + 3. Building it
        // =====================================================================================

        /// <summary>Builds the rail under <paramref name="obj"/> from paths in its mesh's local space. Returns a one-line summary.</summary>
        public static string Build(GameObject obj, List<RailPath> paths)
        {
            Remove(obj);

            int grindLayer = LayerMask.NameToLayer("Grind");
            if (grindLayer < 0) grindLayer = 0;
            int ignoreLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreLayer < 0) ignoreLayer = 0;

            Transform owner = obj.transform;
            var container = new GameObject(ContainerName) { layer = ignoreLayer };
            Undo.RegisterCreatedObjectUndo(container, "Build Grind Rail");
            container.transform.SetParent(owner, false);

            var segmentsRoot = new GameObject("Segments") { layer = ignoreLayer };
            segmentsRoot.transform.SetParent(container.transform, false);

            int railCount = 0, loops = 0, segments = 0;
            float length = 0f;
            foreach (RailPath path in paths)
            {
                if (path.Points.Count < 2) continue;

                // Sizes in meters (the object may be scaled).
                float radius = Mathf.Max(MinBarRadius, path.Radius * Mathf.Abs(owner.lossyScale.x));
                Vector3 lift = owner.InverseTransformVector(Vector3.up * radius); // the top of the bar

                List<Vector3> simplified = Simplify(path.Points, path.Closed);
                segments += BuildSegments(segmentsRoot.transform, owner, simplified, path.Closed, radius, grindLayer, ref length);

                if (path.Closed) loops++;
                AddRail(container.transform, ++railCount, Resample(owner, simplified, path.Closed), lift, ignoreLayer, path.Closed);
            }

            // Rails built by the first version of this tool hid the exported mesh - the mesh is the look now.
            if (obj.TryGetComponent(out MeshRenderer renderer) && !renderer.enabled)
            {
                Undo.RecordObject(renderer, "Build Grind Rail");
                renderer.enabled = true;
            }

            return $"{railCount} grind path(s){(loops > 0 ? $" ({loops} of them loop(s))" : "")}, {segments} segment(s), {length:0.#} m of rail";
        }

        /// <summary>Deletes what <see cref="Build"/> made (and turns the exported mesh back on if an older version hid it).</summary>
        public static void Remove(GameObject obj)
        {
            if (obj == null) return;
            Transform container = obj.transform.Find(ContainerName);
            if (container == null) return;
            Undo.DestroyObjectImmediate(container.gameObject);
            if (obj.TryGetComponent(out MeshRenderer renderer) && !renderer.enabled)
            {
                Undo.RecordObject(renderer, "Remove Grind Rail");
                renderer.enabled = true;
            }
        }

        private static void AddRail(Transform parent, int number, List<Vector3> points, Vector3 lift, int layer, bool loop)
        {
            var go = new GameObject(loop ? $"Rail {number} (loop)" : $"Rail {number}") { layer = layer };
            go.transform.SetParent(parent, false);
            var waypoints = new Vector3[points.Count];
            for (int i = 0; i < points.Count; i++) waypoints[i] = points[i] + lift;
            GrindRail rail = go.AddComponent<GrindRail>();
            rail.Configure(waypoints, RiderOffset, pointsInLocalSpace: true, loop: loop);
        }

        /// <summary>Extra points so they're no more than WaypointSpacing (world meters) apart, like Dan's generated rails. A loop's first point isn't repeated.</summary>
        private static List<Vector3> Resample(Transform owner, List<Vector3> points, bool closed)
        {
            var result = new List<Vector3>();
            int runs = closed ? points.Count : points.Count - 1;
            for (int i = 0; i < runs; i++)
            {
                Vector3 a = points[i], b = points[(i + 1) % points.Count];
                float worldLength = owner.TransformVector(b - a).magnitude;
                int pieces = Mathf.Max(1, Mathf.CeilToInt(worldLength / WaypointSpacing - 1e-3f));
                for (int k = 0; k < pieces; k++) result.Add(Vector3.Lerp(a, b, k / (float)pieces));
            }
            if (!closed && points.Count > 0) result.Add(points[points.Count - 1]);
            return result;
        }

        /// <summary>
        /// Drops points in the middle of straight runs. A loop comes back starting at a corner, without its
        /// first point repeated.
        /// </summary>
        private static List<Vector3> Simplify(List<Vector3> points, bool closed)
        {
            int n = points.Count;
            if (n < 3) return new List<Vector3>(points);

            bool IsCorner(Vector3 before, Vector3 at, Vector3 after)
            {
                Vector3 a = at - before, b = after - at;
                if (a.sqrMagnitude < 1e-8f || b.sqrMagnitude < 1e-8f) return false;
                return Vector3.Dot(a.normalized, b.normalized) < CollinearCos;
            }

            if (!closed)
            {
                var kept = new List<Vector3> { points[0] };
                for (int i = 1; i < n - 1; i++)
                {
                    if (IsCorner(kept[kept.Count - 1], points[i], points[i + 1])) kept.Add(points[i]);
                }
                kept.Add(points[n - 1]);
                return kept;
            }

            var corners = new List<Vector3>();
            for (int i = 0; i < n; i++)
            {
                if (IsCorner(points[(i - 1 + n) % n], points[i], points[(i + 1) % n])) corners.Add(points[i]);
            }
            return corners.Count >= 3 ? corners : new List<Vector3>(points);
        }

        /// <summary>A segment (Grind-layer capsule the bar's thickness) along each straight run, in pieces of up to 5 m.</summary>
        private static int BuildSegments(Transform parent, Transform owner, List<Vector3> localPoints, bool closed, float radius, int layer, ref float totalLength)
        {
            var world = new List<Vector3>();
            foreach (Vector3 p in localPoints) world.Add(owner.TransformPoint(p));
            int runs = closed ? world.Count : world.Count - 1;

            int segments = 0;
            for (int i = 0; i < runs; i++)
            {
                Vector3 a = world[i], b = world[(i + 1) % world.Count];
                float runLength = Vector3.Distance(a, b);
                if (runLength < 1e-4f) continue;
                totalLength += runLength;
                int pieces = Mathf.Max(1, Mathf.CeilToInt(runLength / MaxSegmentLength - 1e-3f));
                for (int k = 0; k < pieces; k++)
                {
                    AddSegment(parent, ++segments, Vector3.Lerp(a, b, k / (float)pieces), Vector3.Lerp(a, b, (k + 1) / (float)pieces), radius, layer);
                }
            }
            return segments;
        }

        private static void AddSegment(Transform parent, int number, Vector3 from, Vector3 to, float radius, int layer)
        {
            Vector3 dir = to - from;
            float length = dir.magnitude;
            var segment = new GameObject(number == 1 ? "grindrailSegment" : $"grindrailSegment ({number - 1})") { layer = layer };
            segment.transform.SetParent(parent, false);
            segment.transform.SetPositionAndRotation(from, Look(dir));
            segment.transform.localScale = Unscale(parent);

            CapsuleCollider capsule = segment.AddComponent<CapsuleCollider>();
            capsule.direction = 2;
            capsule.radius = radius;
            capsule.height = Mathf.Max(length, radius * 2f);
            capsule.center = new Vector3(0f, 0f, length * 0.5f);
        }

        private static Quaternion Look(Vector3 direction)
        {
            if (direction.sqrMagnitude < 1e-8f) return Quaternion.identity;
            Vector3 up = Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(direction, up);
        }

        /// <summary>Undoes the parent's (uniform) scale, so sizes here are in meters.</summary>
        private static Vector3 Unscale(Transform parent)
        {
            float s = parent.lossyScale.x;
            return Mathf.Abs(s) > 1e-6f ? Vector3.one / s : Vector3.one;
        }

        private sealed class UnionFind
        {
            private readonly int[] m_Parent;
            public UnionFind(int count) { m_Parent = new int[count]; for (int i = 0; i < count; i++) m_Parent[i] = i; }
            public int Find(int a)
            {
                while (m_Parent[a] != a) { m_Parent[a] = m_Parent[m_Parent[a]]; a = m_Parent[a]; }
                return a;
            }
            public void Union(int a, int b) { m_Parent[Find(a)] = Find(b); }
        }
    }
}
