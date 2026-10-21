using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Builds the three example challenges Claude designed on 2026-10-21 (Challenge_16_09 "Slush Rush",
    /// Challenge_16_10 "Swing Shift" and Challenge_32_06 "Avalanche") from their glbs, the same way they'd be
    /// put together by hand:
    ///   - a root named after the challenge, with challenge_NN_start / challenge_NN_end at its origin (the
    ///     corner marker and the start / finish rows);
    ///   - each geometry glb (Assets/Maps/challenge_NN_MM*.glb, from TotalEditor3/challenge_NN_MM*.te3) at the
    ///     origin too, given its property with the real Set Object Property tool (Regular, Ice, Wall Climb,
    ///     Grind Rail);
    ///   - pole_4 instances moved, turned and stretched onto each pole's line, then set to Pole (bars and
    ///     upright spin poles for Pole Swing 2);
    ///   - a ChallengeZone with its own ChallengeDefinition (name and points; an existing one is reused, so
    ///     your edits to it survive a rebuild);
    /// then saves the prefab to Assets/Core/Prefabs/Challenges and a scene holding it to
    /// Assets/Core/Scenes/Challenges, and rebuilds the challenge lists.
    ///
    /// Friendslop > Challenges > Build Example Challenges. Safe to run again (it asks before overwriting).
    /// Once you're happy with them this file can be deleted - the prefabs and scenes don't need it.
    /// </summary>
    public static class ExampleChallengeBuilder
    {
        private const string GlbFolder = "Assets/Maps";
        private const string PrefabFolder = "Assets/Core/Prefabs/Challenges";
        private const string SceneFolder = "Assets/Core/Scenes/Challenges";
        private const string DefinitionFolder = "Assets/Core/Data/Challenges";
        private const string PoleGlb = "pole_4";

        private sealed class Piece
        {
            public string Glb;
            public MapObjectPropertyType Type;
            public Piece(string glb, MapObjectPropertyType type) { Glb = glb; Type = type; }
        }

        private sealed class PoleLine
        {
            public string Name;
            public Vector3 From, To; // centre line, Total Editor metres (x, y, z)
            public PoleLine(string name, Vector3 from, Vector3 to) { Name = name; From = from; To = to; }
        }

        private sealed class Spec
        {
            public string Name;
            public int Size;
            public string Title;
            public int Points;
            public Piece[] Pieces;
            public PoleLine[] Poles;
        }

        private static readonly Spec[] Specs =
        {
            new Spec
            {
                Name = "Challenge_16_09", Size = 16, Title = "Slush Rush", Points = 100,
                Pieces = new[]
                {
                    new Piece("challenge_16_09", MapObjectPropertyType.Regular),
                    new Piece("challenge_16_09_ice", MapObjectPropertyType.Ice),
                    new Piece("challenge_16_09_rails", MapObjectPropertyType.GrindRail),
                },
                Poles = new PoleLine[0],
            },
            new Spec
            {
                Name = "Challenge_16_10", Size = 16, Title = "Swing Shift", Points = 100,
                Pieces = new[]
                {
                    new Piece("challenge_16_10", MapObjectPropertyType.Regular),
                },
                Poles = new[]
                {
                    new PoleLine("bar", new Vector3(12f, 9f, 23f), new Vector3(20f, 9f, 23f)),
                    new PoleLine("spin pole 1", new Vector3(13.5f, 2f, 9f), new Vector3(13.5f, 14f, 9f)),
                    new PoleLine("spin pole 2", new Vector3(18f, 2f, 4.5f), new Vector3(18f, 14f, 4.5f)),
                },
            },
            new Spec
            {
                Name = "Challenge_32_06", Size = 32, Title = "Avalanche", Points = 150,
                Pieces = new[]
                {
                    new Piece("challenge_32_06", MapObjectPropertyType.Regular),
                    new Piece("challenge_32_06_climb", MapObjectPropertyType.WallClimb),
                    new Piece("challenge_32_06_ice", MapObjectPropertyType.Ice),
                    new Piece("challenge_32_06_rails", MapObjectPropertyType.GrindRail),
                },
                Poles = new[]
                {
                    new PoleLine("bar left", new Vector3(23f, 9f, 8f), new Vector3(31f, 9f, 8f)),
                    new PoleLine("bar right", new Vector3(33f, 9f, 8f), new Vector3(41f, 9f, 8f)),
                },
            },
        };

        [MenuItem("Friendslop/Challenges/Build Example Challenges")]
        private static void BuildAll()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            AssetDatabase.Refresh();
            var missing = new List<string>();
            foreach (Spec spec in Specs)
            {
                foreach (string glb in GlbsFor(spec))
                {
                    if (AssetDatabase.LoadAssetAtPath<GameObject>(GlbPath(glb)) == null && !missing.Contains(glb)) missing.Add(glb);
                }
            }
            if (missing.Count > 0)
            {
                EditorUtility.DisplayDialog("Build Example Challenges",
                    $"These glbs aren't in {GlbFolder} (or haven't imported yet):\n\n{string.Join("\n", missing)}", "OK");
                return;
            }

            var existing = new List<string>();
            foreach (Spec spec in Specs)
            {
                if (File.Exists(PrefabPath(spec)) || File.Exists(ScenePath(spec))) existing.Add(spec.Name);
            }
            if (existing.Count > 0 && !EditorUtility.DisplayDialog("Build Example Challenges",
                    $"This replaces the prefab and scene of:\n\n{string.Join("\n", existing)}\n\nTheir ChallengeDefinition assets are kept.", "Rebuild", "Cancel"))
            {
                return;
            }

            var report = new StringBuilder();
            foreach (Spec spec in Specs)
            {
                try
                {
                    report.AppendLine(Build(spec));
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    report.AppendLine($"{spec.Name}: failed - {e.Message}");
                }
            }

            AssetDatabase.SaveAssets();
            report.AppendLine();
            report.AppendLine(MatchCatalogBuilder.Rebuild());
            Debug.Log("[Friendslop] Build Example Challenges:\n" + report);
            EditorUtility.DisplayDialog("Build Example Challenges", report.ToString(), "OK");
        }

        private static IEnumerable<string> GlbsFor(Spec spec)
        {
            yield return $"challenge_{spec.Size}_start";
            yield return $"challenge_{spec.Size}_end";
            foreach (Piece piece in spec.Pieces) yield return piece.Glb;
            if (spec.Poles.Length > 0) yield return PoleGlb;
        }

        private static string GlbPath(string glb) => $"{GlbFolder}/{glb}.glb";
        private static string PrefabPath(Spec spec) => $"{PrefabFolder}/{spec.Name}.prefab";
        private static string ScenePath(Spec spec) => $"{SceneFolder}/{spec.Name}.unity";

        private static string Build(Spec spec)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject(spec.Name);
            int problems = 0;

            Place($"challenge_{spec.Size}_start", root);
            Place($"challenge_{spec.Size}_end", root);

            foreach (Piece piece in spec.Pieces)
            {
                GameObject obj = Place(piece.Glb, root);
                if (!MapObjectPropertySetup.ApplyProperty(obj, piece.Type)) problems++;
            }

            foreach (PoleLine line in spec.Poles)
            {
                GameObject pole = Place(PoleGlb, root);
                pole.name = line.Name;
                FitPole(pole, line.From, line.To);
                if (!MapObjectPropertySetup.ApplyProperty(pole, MapObjectPropertyType.Pole)) problems++;
            }

            AddZone(root, spec);

            GameObject prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(root, PrefabPath(spec), InteractionMode.AutomatedAction, out bool saved);
            if (!saved || prefab == null) return $"{spec.Name}: couldn't save {PrefabPath(spec)}";

            EditorSceneManager.SaveScene(scene, ScenePath(spec));
            return $"{spec.Name} \"{spec.Title}\": built ({spec.Pieces.Length} pieces, {spec.Poles.Length} poles)" +
                   (problems > 0 ? $" - {problems} piece(s) couldn't be set up, see the Console" : "");
        }

        /// <summary>A glb instance (still linked to the glb, so re-exports flow through) at the challenge's origin.</summary>
        private static GameObject Place(string glb, GameObject root)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(GlbPath(glb));
            if (asset == null) throw new FileNotFoundException($"Missing {GlbPath(glb)}");

            var obj = (GameObject)PrefabUtility.InstantiatePrefab(asset, root.transform);
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;
            return obj;
        }

        /// <summary>
        /// Lines a pole glb (an upright bar, long along its own Y) up with <paramref name="from"/> → <paramref name="to"/>
        /// (Total Editor metres; the glb import mirrors x, so x is negated): turned to point along the line, stretched
        /// along its length to match, centred on the line's middle.
        /// </summary>
        private static void FitPole(GameObject pole, Vector3 from, Vector3 to)
        {
            Vector3 a = new Vector3(-from.x, from.y, from.z);
            Vector3 b = new Vector3(-to.x, to.y, to.z);
            Vector3 along = b - a;
            float length = along.magnitude;

            MeshFilter filter = pole.GetComponentInChildren<MeshFilter>();
            Bounds bounds = filter.sharedMesh.bounds;

            Transform t = pole.transform;
            t.localRotation = Quaternion.FromToRotation(Vector3.up, along / length);
            t.localScale = new Vector3(1f, length / Mathf.Max(0.01f, bounds.size.y), 1f);
            t.localPosition = Vector3.zero;

            // The challenge root sits at the scene origin, so world and challenge space are the same here.
            Vector3 centreNow = filter.transform.TransformPoint(bounds.center);
            t.localPosition = (a + b) * 0.5f - centreNow;
        }

        private static void AddZone(GameObject root, Spec spec)
        {
            string path = $"{DefinitionFolder}/{spec.Name}Definition.asset";
            var definition = AssetDatabase.LoadAssetAtPath<ChallengeDefinition>(path);
            if (definition == null)
            {
                if (!AssetDatabase.IsValidFolder(DefinitionFolder))
                {
                    Directory.CreateDirectory(DefinitionFolder);
                    AssetDatabase.Refresh();
                }
                definition = ScriptableObject.CreateInstance<ChallengeDefinition>();
                definition.challengeName = spec.Title;
                definition.basePoints = spec.Points;
                AssetDatabase.CreateAsset(definition, path);
            }

            var zone = root.AddComponent<ChallengeZone>();
            var serialized = new SerializedObject(zone);
            serialized.FindProperty("definition").objectReferenceValue = definition;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
