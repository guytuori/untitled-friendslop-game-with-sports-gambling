using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The dinosaur models that wander around maps (see <see cref="Dinosaurs"/>): for each model, its mesh
    /// (how a placed dinosaur is recognized in a map), its idle, walk, attack and jump animations, plus the
    /// sound a dinosaur makes when a player presses Interact next to it (the T-rex also roars it).
    ///
    /// Built automatically in the editor by DinosaurCatalogBuilder from the FBX models in
    /// Assets/_models/Dinosaur Animated Pack and stored at Resources/DinosaurCatalog so the game can load
    /// it - don't edit it by hand.
    /// </summary>
    public class DinosaurCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("The model's file name, e.g. \"Trex\".")]
            public string id;

            public GameObject model;

            [Tooltip("The model's mesh - a skinned mesh in a map using this mesh is this dinosaur.")]
            public Mesh mesh;

            public AnimationClip idle;
            public AnimationClip walk;

            [Tooltip("The raptors' occasional lunge.")]
            public AnimationClip attack;

            [Tooltip("The T-rex's occasional stomp-and-roar.")]
            public AnimationClip jump;
        }

        public const string ResourcePath = "DinosaurCatalog";

        /// <summary>Bumped when the entries gain fields, so the editor rebuilds an older catalog by itself.</summary>
        public const int CurrentVersion = 2;

        [SerializeField, HideInInspector] private int version;

        [SerializeField] private List<Entry> dinosaurs = new List<Entry>();

        [Tooltip("Played by a dinosaur when a player presses Interact next to it.")]
        [SerializeField] private AudioClip interactSound;

        private Dictionary<Mesh, Entry> m_ByMesh;

        public IReadOnlyList<Entry> Dinosaurs => dinosaurs;
        public AudioClip InteractSound => interactSound;
        public int Version => version;

        /// <summary>The catalog, or null if it hasn't been built yet.</summary>
        public static DinosaurCatalog Load() => Resources.Load<DinosaurCatalog>(ResourcePath);

        /// <summary>The dinosaur whose model uses <paramref name="mesh"/>, or null.</summary>
        public Entry FindByMesh(Mesh mesh)
        {
            if (mesh == null) return null;
            if (m_ByMesh == null)
            {
                m_ByMesh = new Dictionary<Mesh, Entry>();
                foreach (Entry entry in dinosaurs)
                {
                    if (entry != null && entry.mesh != null) m_ByMesh[entry.mesh] = entry;
                }
            }
            return m_ByMesh.TryGetValue(mesh, out Entry found) ? found : null;
        }

#if UNITY_EDITOR
        /// <summary>Editor only - used by DinosaurCatalogBuilder.</summary>
        public void Set(List<Entry> entries, AudioClip sound)
        {
            dinosaurs = entries;
            interactSound = sound;
            version = CurrentVersion;
            m_ByMesh = null;
        }
#endif
    }
}
