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
    ///   - A map marks each hole with a <c>challenge16</c> or <c>challenge32</c> entity in the hole's corner
    ///     cell (where the old marker cube used to be). That entity is the socket.
    ///   - A challenge's start/finish pieces carry the same-named entity at the same corner of the challenge.
    /// Placing a challenge = moving its root so its marker lands exactly on the socket (position and
    /// rotation), so the challenge prefab's own pivot doesn't matter.
    ///
    /// Sockets are found in hierarchy order, which is identical on every client for the same map prefab -
    /// that order is how <see cref="MatchLayout"/> matches its networked picks to holes.
    /// </summary>
    public static class ChallengeSockets
    {
        /// <summary>One hole in a map: the marker entity's Transform and the challenge size it takes (16 or 32).</summary>
        public readonly struct Socket
        {
            public readonly Transform Transform;
            public readonly int Size;

            public Socket(Transform transform, int size)
            {
                Transform = transform;
                Size = size;
            }
        }

        // "challenge16" / "challenge32", optionally with Unity's " (1)"-style duplicate suffix. Not
        // "challengestart"/"challengefinish" (those mark a challenge's start/finish rows, not its corner).
        private static readonly Regex MarkerName = new Regex(@"^challenge(16|32)(\s*\(\d+\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>True if <paramref name="name"/> is a challenge corner marker; <paramref name="size"/> is 16 or 32.</summary>
        public static bool TryParseMarker(string name, out int size)
        {
            size = 0;
            if (string.IsNullOrEmpty(name)) return false;
            Match match = MarkerName.Match(name.Trim());
            if (!match.Success) return false;
            size = match.Groups[1].Value == "16" ? 16 : 32;
            return true;
        }

        /// <summary>Every socket under <paramref name="mapRoot"/>, in hierarchy order (the same on every client).</summary>
        public static List<Socket> FindSockets(Transform mapRoot)
        {
            var sockets = new List<Socket>();
            if (mapRoot == null) return sockets;

            foreach (Transform t in mapRoot.GetComponentsInChildren<Transform>(true))
            {
                if (TryParseMarker(t.name, out int size)) sockets.Add(new Socket(t, size));
            }
            return sockets;
        }

        /// <summary>The first corner marker inside a challenge (its start and finish pieces both have one, in the same spot).</summary>
        public static Transform FindMarker(Transform challengeRoot, out int size)
        {
            size = 0;
            if (challengeRoot == null) return null;

            foreach (Transform t in challengeRoot.GetComponentsInChildren<Transform>(true))
            {
                if (t != challengeRoot && TryParseMarker(t.name, out size)) return t;
            }
            return null;
        }

        /// <summary>
        /// Moves <paramref name="challengeRoot"/> so that <paramref name="marker"/> (a descendant of it) ends up
        /// exactly on <paramref name="socket"/>, in position and rotation. Assumes unit scale, like the maps.
        /// </summary>
        public static void Align(Transform challengeRoot, Transform marker, Transform socket)
        {
            if (challengeRoot == null || marker == null || socket == null) return;

            // The marker's pose relative to the challenge root...
            Quaternion inverseRoot = Quaternion.Inverse(challengeRoot.rotation);
            Quaternion localRotation = inverseRoot * marker.rotation;
            Vector3 localPosition = inverseRoot * (marker.position - challengeRoot.position);

            // ...applied backwards from the socket.
            Quaternion rootRotation = socket.rotation * Quaternion.Inverse(localRotation);
            challengeRoot.SetPositionAndRotation(socket.position - rootRotation * localPosition, rootRotation);
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

            Align(instance.transform, marker, socket.Transform);
            return instance;
        }

        private static void DestroyAny(GameObject go)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(go);
            else UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
