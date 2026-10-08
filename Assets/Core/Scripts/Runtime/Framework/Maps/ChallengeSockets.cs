using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The "holes" in a map that challenges get dropped into, and how a challenge is lined up with one.
    ///
    /// Both sides are marked with Total Editor entities, which the glTF importer brings in as empty child
    /// GameObjects named after the entity:
    ///   - A map marks each hole with FOUR entities, one in each of the hole's corner cels: three
    ///     <c>challenge16</c> (or <c>challenge32</c>) and one <c>challenge16corner</c> (or <c>challenge32corner</c>).
    ///     Any four same-size markers that form a square of the right size (corner cel centers (size - 1) cels
    ///     apart, on the same level) are one hole - so it doesn't matter how the map was split into pieces and
    ///     put back together in Unity, or which piece a marker ended up in. Keep holes far enough apart that
    ///     their corners can't form a square with a neighbor's.
    ///   - A challenge's start/finish pieces carry one <c>challenge16</c>/<c>challenge32</c> entity at the
    ///     challenge's own corner (its lowest x and z in Total Editor; the challenge extends from there).
    /// Placing a challenge = putting the challenge's corner on the hole's CORNER marker and turning it (in 90
    /// degree steps) so the challenge lies inside the square. So the corner marker picks which way each
    /// hole's challenge faces. A square without a corner marker uses its corner nearest Total Editor's grid
    /// origin, unturned (the same as the challenges themselves).
    ///
    /// OLD MAPS: if a map has no complete square at all for a size, every marker of that size is treated as
    /// a hole on its own (the original one-marker-per-hole layout). Once a map has at least one square for a
    /// size, stray markers of that size that aren't part of a square are ignored (with a warning).
    ///
    /// Holes come out in the hierarchy order of their corner markers, which is identical on every client for
    /// the same map prefab - that order is how <see cref="MatchLayout"/> matches its networked picks to holes.
    /// </summary>
    public static class ChallengeSockets
    {
        /// <summary>
        /// One hole in a map: the corner marker the challenge's corner goes on, the challenge size it takes
        /// (16 or 32), and the rotation the challenge gets there (world space).
        /// </summary>
        public readonly struct Socket
        {
            public readonly Transform Transform;
            public readonly int Size;
            public readonly Quaternion Rotation;

            public Socket(Transform transform, int size) : this(transform, size, transform != null ? transform.rotation : Quaternion.identity) { }

            public Socket(Transform transform, int size, Quaternion rotation)
            {
                Transform = transform;
                Size = size;
                Rotation = rotation;
            }
        }

        // "challenge16" / "challenge32" / "challenge16corner" / "challenge32corner", optionally with Unity's
        // " (1)"-style duplicate suffix. Not "challengestart"/"challengefinish" (a challenge's start/finish rows).
        private static readonly Regex MarkerName = new Regex(@"^challenge(16|32)(corner)?(\s*\(\d+\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>True if <paramref name="name"/> is a challenge marker (plain or corner); <paramref name="size"/> is 16 or 32.</summary>
        public static bool TryParseMarker(string name, out int size) => TryParseMarker(name, out size, out _);

        /// <summary>As above; <paramref name="isCorner"/> is true for challenge16corner / challenge32corner.</summary>
        public static bool TryParseMarker(string name, out int size, out bool isCorner)
        {
            size = 0;
            isCorner = false;
            if (string.IsNullOrEmpty(name)) return false;
            Match match = MarkerName.Match(name.Trim());
            if (!match.Success) return false;
            size = match.Groups[1].Value == "16" ? 16 : 32;
            isCorner = match.Groups[2].Success;
            return true;
        }

        /// <summary>How far two corner markers may be from an exact square and still count (meters).</summary>
        private const float SquareTolerance = 0.3f;

        private struct MarkerInfo
        {
            public Transform Transform;
            public int Size;
            public int Order;      // hierarchy order
            public Vector3 Local;  // position in the map root's space
            public bool IsCorner;  // challenge16corner / challenge32corner
        }

        /// <summary>Every hole under <paramref name="mapRoot"/>, in hierarchy order of their corner markers (the same on every client).</summary>
        public static List<Socket> FindSockets(Transform mapRoot)
        {
            var result = new List<(int Order, Socket Socket)>();
            if (mapRoot == null) return new List<Socket>();

            var markers = new List<MarkerInfo>();
            int order = 0;
            foreach (Transform t in mapRoot.GetComponentsInChildren<Transform>(true))
            {
                order++;
                if (t == mapRoot || !TryParseMarker(t.name, out int size, out bool isCorner)) continue;
                markers.Add(new MarkerInfo { Transform = t, Size = size, Order = order, Local = mapRoot.InverseTransformPoint(t.position), IsCorner = isCorner });
            }
            if (markers.Count == 0) return new List<Socket>();

            Vector2 teSigns = EditorAxisSigns(markers);

            foreach (int size in new[] { 16, 32 })
            {
                var ofSize = markers.FindAll(m => m.Size == size);
                if (ofSize.Count == 0) continue;

                float side = (size - 1) * TotalEditorCelSize;
                var used = new bool[ofSize.Count];
                var squares = new List<(int Order, Socket Socket)>();

                // Every square has exactly one corner with the lowest x and z in the map's space - try each
                // marker as that corner and look for the other three.
                for (int a = 0; a < ofSize.Count; a++)
                {
                    if (used[a]) continue;
                    Vector3 p = ofSize[a].Local;
                    int b = FindAt(ofSize, used, p + new Vector3(side, 0f, 0f), a);
                    int c = FindAt(ofSize, used, p + new Vector3(0f, 0f, side), a);
                    int d = FindAt(ofSize, used, p + new Vector3(side, 0f, side), a);
                    if (b < 0 || c < 0 || d < 0 || b == c || b == d || c == d) continue;

                    int[] corners = { a, b, c, d };
                    foreach (int i in corners) used[i] = true;

                    // The hole's corner the challenge's corner goes on: the corner marker if there is one,
                    // otherwise the corner nearest Total Editor's grid origin.
                    int origin = -1;
                    int cornerMarkers = 0;
                    foreach (int i in corners)
                    {
                        if (!ofSize[i].IsCorner) continue;
                        cornerMarkers++;
                        if (origin < 0) origin = i;
                    }
                    if (cornerMarkers > 1)
                    {
                        Vector3 l = ofSize[a].Local;
                        Debug.LogWarning($"[Challenges] The {size}x{size} hole near ({l.x:0.#}, {l.y:0.#}, {l.z:0.#}) in '{mapRoot.name}' has {cornerMarkers} " +
                                         $"challenge{size}corner markers - it should have exactly one. Using '{ofSize[origin].Transform.name}'.", ofSize[origin].Transform);
                    }
                    if (origin < 0)
                    {
                        origin = a;
                        float best = float.MaxValue;
                        foreach (int i in corners)
                        {
                            Vector3 l = ofSize[i].Local;
                            float score = teSigns.x * l.x + teSigns.y * l.z;
                            if (score < best - 0.001f) { best = score; origin = i; }
                        }
                    }

                    // Turn the challenge so it lies inside the square. A challenge extends from its corner
                    // towards Total Editor's +x and +z; inside the hole, "inwards" from the origin corner is
                    // towards the square's center.
                    Vector3 center = (ofSize[a].Local + ofSize[b].Local + ofSize[c].Local + ofSize[d].Local) * 0.25f;
                    Vector3 inward = center - ofSize[origin].Local;
                    var challengeInward = new Vector3(teSigns.x, 0f, teSigns.y);
                    var holeInward = new Vector3(Mathf.Sign(inward.x), 0f, Mathf.Sign(inward.z));
                    float yaw = QuarterTurnBetween(challengeInward, holeInward);
                    Quaternion rotation = mapRoot.rotation * Quaternion.Euler(0f, yaw, 0f);

                    int first = Mathf.Min(Mathf.Min(ofSize[a].Order, ofSize[b].Order), Mathf.Min(ofSize[c].Order, ofSize[d].Order));
                    squares.Add((first, new Socket(ofSize[origin].Transform, size, rotation)));
                }

                if (squares.Count == 0)
                {
                    // An old-style map: one marker per hole.
                    foreach (MarkerInfo m in ofSize) result.Add((m.Order, new Socket(m.Transform, size)));
                    continue;
                }

                result.AddRange(squares);
                for (int i = 0; i < ofSize.Count; i++)
                {
                    if (used[i]) continue;
                    Vector3 l = ofSize[i].Local;
                    Debug.LogWarning($"[Challenges] '{ofSize[i].Transform.name}' at ({l.x:0.#}, {l.y:0.#}, {l.z:0.#}) in '{mapRoot.name}' " +
                                     $"isn't one of four corners of a {size}x{size} square - ignored.", ofSize[i].Transform);
                }
            }

            result.Sort((x, y) => x.Order.CompareTo(y.Order));
            return result.ConvertAll(r => r.Socket);
        }

        /// <summary>The quarter turn about y (0, 90, 180 or 270 degrees) that takes diagonal <paramref name="from"/> to diagonal <paramref name="to"/>.</summary>
        private static float QuarterTurnBetween(Vector3 from, Vector3 to)
        {
            for (int quarter = 0; quarter < 4; quarter++)
            {
                Vector3 turned = Quaternion.Euler(0f, quarter * 90f, 0f) * from;
                if (Mathf.Sign(turned.x) == Mathf.Sign(to.x) && Mathf.Sign(turned.z) == Mathf.Sign(to.z)) return quarter * 90f;
            }
            return 0f;
        }

        /// <summary>The unused marker (other than <paramref name="except"/>) within tolerance of <paramref name="target"/>, or -1.</summary>
        private static int FindAt(List<MarkerInfo> markers, bool[] used, Vector3 target, int except)
        {
            for (int i = 0; i < markers.Count; i++)
            {
                if (i == except || used[i]) continue;
                if ((markers[i].Local - target).sqrMagnitude <= SquareTolerance * SquareTolerance) return i;
            }
            return -1;
        }

        /// <summary>
        /// Total Editor's grid coordinates are all positive; the glTF import may mirror an axis (UnityGLTF flips x).
        /// Each marker's position relative to its own glb root is its editor position (possibly mirrored), so the
        /// sign of most of them tells which way each axis runs: +1 = same as the editor, -1 = mirrored.
        /// </summary>
        private static Vector2 EditorAxisSigns(List<MarkerInfo> markers)
        {
            int x = 0, z = 0;
            foreach (MarkerInfo m in markers)
            {
                Vector3 l = m.Transform.localPosition;
                x += l.x < 0f ? -1 : 1;
                z += l.z < 0f ? -1 : 1;
            }
            return new Vector2(x < 0 ? -1f : 1f, z < 0 ? -1f : 1f);
        }

        /// <summary>
        /// A challenge's corner marker (its start and finish pieces both have one, in the same spot). If it has
        /// several - e.g. one in every corner, like the maps - the one nearest Total Editor's grid origin.
        /// </summary>
        public static Transform FindMarker(Transform challengeRoot, out int size)
        {
            size = 0;
            if (challengeRoot == null) return null;

            var markers = new List<MarkerInfo>();
            foreach (Transform t in challengeRoot.GetComponentsInChildren<Transform>(true))
            {
                if (t != challengeRoot && TryParseMarker(t.name, out int s))
                {
                    markers.Add(new MarkerInfo { Transform = t, Size = s, Local = challengeRoot.InverseTransformPoint(t.position) });
                }
            }
            if (markers.Count == 0) return null;

            Vector2 teSigns = EditorAxisSigns(markers);
            MarkerInfo best = markers[0];
            float bestScore = float.MaxValue;
            foreach (MarkerInfo m in markers)
            {
                float score = teSigns.x * m.Local.x + teSigns.y * m.Local.z;
                if (score < bestScore - 0.001f) { bestScore = score; best = m; }
            }
            size = best.Size;
            return best.Transform;
        }

        /// <summary>
        /// Moves <paramref name="challengeRoot"/> so that <paramref name="marker"/> (a descendant of it) ends up
        /// exactly on <paramref name="socket"/>, in position and rotation. Assumes unit scale, like the maps.
        /// </summary>
        public static void Align(Transform challengeRoot, Transform marker, Transform socket)
        {
            if (socket == null) return;
            Align(challengeRoot, marker, socket.position, socket.rotation);
        }

        /// <summary>As above, onto a position and rotation (a hole's corner, turned to fit its square).</summary>
        public static void Align(Transform challengeRoot, Transform marker, Vector3 socketPosition, Quaternion socketRotation)
        {
            if (challengeRoot == null || marker == null) return;

            // The marker's pose relative to the challenge root...
            Quaternion inverseRoot = Quaternion.Inverse(challengeRoot.rotation);
            Quaternion localRotation = inverseRoot * marker.rotation;
            Vector3 localPosition = inverseRoot * (marker.position - challengeRoot.position);

            // ...applied backwards from the socket.
            Quaternion rootRotation = socketRotation * Quaternion.Inverse(localRotation);
            challengeRoot.SetPositionAndRotation(socketPosition - rootRotation * localPosition, rootRotation);
        }

        /// <summary>
        /// Instantiates <paramref name="challengePrefab"/> under <paramref name="parent"/> and lines it up with
        /// <paramref name="socket"/>. Returns null (and destroys the copy) if the challenge has no corner marker.
        /// </summary>
        public static GameObject Place(GameObject challengePrefab, Socket socket, Transform parent, string instanceName)
        {
            if (challengePrefab == null || socket.Transform == null) return null;

            GameObject instance = UnityEngine.Object.Instantiate(challengePrefab, parent);
            if (!string.IsNullOrEmpty(instanceName)) instance.name = instanceName;

            Transform marker = FindMarker(instance.transform, out int size);
            if (marker == null)
            {
                Debug.LogWarning($"[Challenges] '{challengePrefab.name}' has no challenge16/challenge32 marker - can't place it.", challengePrefab);
                DestroyAny(instance);
                return null;
            }
            if (size != socket.Size)
            {
                Debug.LogWarning($"[Challenges] '{challengePrefab.name}' is a {size}x{size} challenge but socket '{socket.Transform.name}' takes {socket.Size}x{socket.Size}.", challengePrefab);
            }

            if (IsEntityMarker(marker))
            {
                Align(instance.transform, marker, socket.Transform.position, socket.Rotation);
            }
            else
            {
                // No marker entity, just a glb whose ROOT is named challenge16/32 (e.g. the plain Practice Map
                // pieces). A real marker sits in the corner cel's center, half a cel in from the glb's origin
                // on every axis, so line that point up with the socket instead of the glb's origin.
                // (Which way is "in" depends on whether the import mirrored the axis - see GeometrySigns.)
                Vector2 signs = GeometrySigns(marker);
                float half = TotalEditorCelSize * 0.5f;
                var virtualMarker = new GameObject("CornerMarker (virtual)").transform;
                virtualMarker.SetParent(marker, false);
                virtualMarker.localPosition = new Vector3(signs.x * half, half, signs.y * half);
                Align(instance.transform, virtualMarker, socket.Transform.position, socket.Rotation);
                DestroyAny(virtualMarker.gameObject);
            }
            return instance;
        }

        /// <summary>Total Editor's default grid spacing (meters per cel).</summary>
        public const float TotalEditorCelSize = 2f;

        /// <summary>
        /// For a glb root used as a marker: its geometry sits on the positive side of the origin in Total Editor,
        /// so the side its geometry is actually on (in the root's own space) tells whether the import mirrored x / z.
        /// </summary>
        private static Vector2 GeometrySigns(Transform glbRoot)
        {
            bool any = false;
            Bounds bounds = default;
            foreach (MeshFilter filter in glbRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                Bounds b = filter.sharedMesh.bounds;
                Vector3 center = glbRoot.InverseTransformPoint(filter.transform.TransformPoint(b.center));
                if (!any) { bounds = new Bounds(center, Vector3.zero); any = true; }
                else bounds.Encapsulate(center);
            }
            if (!any) return Vector2.one;
            return new Vector2(bounds.center.x < 0f ? -1f : 1f, bounds.center.z < 0f ? -1f : 1f);
        }

        /// <summary>A marker entity comes in as an empty GameObject; a glb's root has the geometry under it.</summary>
        private static bool IsEntityMarker(Transform marker) =>
            marker.childCount == 0 && marker.GetComponent<MeshFilter>() == null && marker.GetComponent<Renderer>() == null;

        private static void DestroyAny(GameObject go)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
