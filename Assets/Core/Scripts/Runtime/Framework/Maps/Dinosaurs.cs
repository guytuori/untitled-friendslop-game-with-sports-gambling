using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Wandering dinosaurs - scenery for the Dinosaur Zoo (or any map). Any model from the
    /// <see cref="DinosaurCatalog"/> (Assets/_models/Dinosaur Animated Pack) placed in a map prefab is found
    /// when <see cref="MatchLayout"/> builds the map and gets a <see cref="DinosaurWander"/>, which walks it
    /// around near where it was placed. They aren't hazards: they have no colliders, so players pass through.
    ///
    /// Where they may go (checked by DinosaurWander): solid, fairly level ground - not off edges, into pits
    /// or water, or up/down steps - and never into a challenge hole (each hole's square, plus whatever
    /// challenge was dropped into it, plus <see cref="HoleMargin"/>). If one ends up somewhere it shouldn't
    /// anyway, it's put back where it was placed.
    ///
    /// Interact (E / R1 by default) within <see cref="InteractRange"/> of a dinosaur climbs onto its back for a
    /// ride (<see cref="DinosaurRider"/>, 2026-11-08) and it roars (dinosaur.mp3, for everyone through MatchLayout's
    /// RPC). Each one has an InteractMarker (a spinning arrow over its head) to show it can be interacted with -
    /// hidden on the one this machine's player is riding.
    ///
    /// Networked (2026-11-08): the session's master client walks them all and MatchLayout sends where each one is
    /// and what it's doing (<see cref="DinosaurNetState"/>); everyone else's copies follow (<see cref="HasAuthority"/>).
    /// Before that, each machine walked its own copies and they were in different places for everyone.
    /// </summary>
    public static class Dinosaurs
    {
        /// <summary>How close (meters, to the dinosaur's bounding box) a player has to be for Interact.</summary>
        public const float InteractRange = 3f;

        /// <summary>
        /// The dinosaurs are shrunk to this size as the map is built (2026-11-08: at their modelled size they were about
        /// twice as big as real ones next to a person; the user wanted them a quarter smaller but still big).
        /// </summary>
        public const float SizeScale = 0.75f;

        /// <summary>Extra keep-out space around each challenge hole, in meters.</summary>
        public const float HoleMargin = 3f;

        private static readonly List<DinosaurWander> s_All = new List<DinosaurWander>();
        private static readonly List<Rect> s_Forbidden = new List<Rect>();
        private static AudioClip s_Sound;

        public static IReadOnlyList<DinosaurWander> All => s_All;

        /// <summary>dinosaur.mp3 - the Interact sound, also the T-rex's roar.</summary>
        public static AudioClip Sound => s_Sound;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            MatchLayout.Built -= OnMatchBuilt;
            MatchLayout.Built += OnMatchBuilt;
        }

        private static void OnMatchBuilt(MatchLayout layout)
        {
            s_All.RemoveAll(d => d == null);
            GameObject map = layout != null ? layout.MapInstance : null;
            if (map == null) return;

            DinosaurCatalog catalog = DinosaurCatalog.Load();
            if (catalog == null || catalog.Dinosaurs.Count == 0) return;
            s_Sound = catalog.InteractSound;

            // Find the dinosaurs first (so nothing is added before the keep-out areas exist).
            var found = new List<(Transform Root, DinosaurCatalog.Entry Entry)>();
            foreach (SkinnedMeshRenderer skin in map.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                DinosaurCatalog.Entry entry = catalog.FindByMesh(skin.sharedMesh);
                if (entry == null) continue;

                Transform root = ModelRoot(skin.transform, map.transform);
                if (root.GetComponent<DinosaurWander>() != null || found.Exists(f => f.Root == root)) continue;
                found.Add((root, entry));
            }
            if (found.Count == 0) return;

            BuildForbiddenAreas(layout, map.transform);

            s_All.Clear();
            for (int i = 0; i < found.Count; i++)
            {
                // Smaller first, so everything DinosaurWander measures (body, stride, saddle) is at the new size.
                found[i].Root.localScale *= SizeScale;
                var wander = found[i].Root.gameObject.AddComponent<DinosaurWander>();
                wander.Init(i, found[i].Entry);
                s_All.Add(wander);

                // "This can be interacted with": an arrow over its head, following it around.
                DinosaurWander dinosaur = wander;
                if (InteractMarker.TryGetRendererBounds(found[i].Root, out Bounds bounds)) InteractMarker.Add(found[i].Root, bounds, () => !DinosaurRider.IsLocalPlayerRiding(dinosaur));
            }

            // One listener for the Interact button, living (and dying) with the map.
            if (map.GetComponent<DinosaurInteract>() == null) map.AddComponent<DinosaurInteract>();

            Debug.Log($"[Dinosaurs] {found.Count} dinosaur(s) wandering in '{map.name}'.");
        }

        /// <summary>The placed model's root: the object with its Animator, or the mesh's parent.</summary>
        private static Transform ModelRoot(Transform mesh, Transform mapRoot)
        {
            Animator animator = mesh.GetComponentInParent<Animator>(true);
            if (animator != null && animator.transform != mapRoot && animator.transform.IsChildOf(mapRoot)) return animator.transform;
            return mesh.parent != null && mesh.parent != mapRoot ? mesh.parent : mesh;
        }

        private static void BuildForbiddenAreas(MatchLayout layout, Transform mapRoot)
        {
            s_Forbidden.Clear();

            foreach (ChallengeSockets.Socket socket in ChallengeSockets.FindSockets(mapRoot))
            {
                if (socket.HasArea) AddForbidden(socket.Area);
            }

            // Whatever was dropped into the holes (also covers old-style single-marker holes).
            Transform challenges = layout.transform.Find("Challenges");
            if (challenges == null) return;
            foreach (Transform challenge in challenges)
            {
                Bounds? bounds = null;
                foreach (Renderer r in challenge.GetComponentsInChildren<Renderer>(true))
                {
                    if (bounds == null) bounds = r.bounds;
                    else { Bounds b = bounds.Value; b.Encapsulate(r.bounds); bounds = b; }
                }
                if (bounds != null) AddForbidden(bounds.Value);
            }
        }

        private static void AddForbidden(Bounds area)
        {
            s_Forbidden.Add(Rect.MinMaxRect(area.min.x - HoleMargin, area.min.z - HoleMargin, area.max.x + HoleMargin, area.max.z + HoleMargin));
        }

        /// <summary>True if <paramref name="point"/> is in (or near) a challenge hole, at any height.</summary>
        public static bool IsForbidden(Vector3 point)
        {
            var p = new Vector2(point.x, point.z);
            for (int i = 0; i < s_Forbidden.Count; i++)
            {
                if (s_Forbidden[i].Contains(p)) return true;
            }
            return false;
        }

        /// <summary>
        /// True on the machine that runs the dinosaurs: offline / before the match is networked, or the session's
        /// master client (MatchLayout's state authority). Everyone else's dinosaurs follow MatchLayout's state.
        /// </summary>
        public static bool HasAuthority
        {
            get
            {
                MatchLayout layout = MatchLayout.Current;
                return layout == null || layout.HasDinosaurAuthority;
            }
        }

        /// <summary>What the master last sent about dinosaur <paramref name="index"/> (false if nothing yet).</summary>
        public static bool TryGetNetState(int index, out DinosaurNetState state)
        {
            MatchLayout layout = MatchLayout.Current;
            state = default;
            return layout != null && layout.TryGetDinosaurState(index, out state);
        }

        /// <summary>The dinosaur at <paramref name="index"/> in <see cref="All"/>, or null.</summary>
        public static DinosaurWander Get(int index) => index >= 0 && index < s_All.Count ? s_All[index] : null;

        /// <summary>The nearest dinosaur within Interact range of <paramref name="point"/>, or -1.</summary>
        public static int FindNear(Vector3 point)
        {
            int best = -1;
            float bestDistance = InteractRange;
            for (int i = 0; i < s_All.Count; i++)
            {
                if (s_All[i] == null) continue;
                float distance = s_All[i].DistanceTo(point);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>Interact next to dinosaur <paramref name="index"/>: its sound, for everyone.</summary>
        public static void RequestSound(int index)
        {
            DinosaurWander dinosaur = index >= 0 && index < s_All.Count ? s_All[index] : null;
            if (dinosaur == null || s_Sound == null || dinosaur.IsMakingSound) return;

            MatchLayout layout = MatchLayout.Current;
            if (layout != null && layout.IsSpawned) layout.RPC_PlayDinosaurSound(index);
            else PlaySound(index);
        }

        /// <summary>Plays dinosaur <paramref name="index"/>'s sound on this machine (called on everyone by the RPC).</summary>
        public static void PlaySound(int index)
        {
            DinosaurWander dinosaur = index >= 0 && index < s_All.Count ? s_All[index] : null;
            if (dinosaur != null) dinosaur.MakeSound(s_Sound);
        }
    }
}
