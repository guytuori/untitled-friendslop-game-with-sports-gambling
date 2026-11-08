using UnityEngine;
using UnityEngine.Rendering;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The small red 3D arrow over the player's head while they're on a pole (Pole Swing 2, added 2026-11-08): it
    /// points the way a jump off the pole would send them right now, so it's clear when to let go.
    /// Made and moved by <see cref="PoleSwing2Ability"/>, for the local player only. No assets: the mesh is built
    /// here and drawn with Sprites/Default (always in a build), coloured through its vertex colours.
    /// </summary>
    public class PoleJumpArrow : MonoBehaviour
    {
        /// <summary>Tip to tail (meters).</summary>
        public const float Length = 0.7f;

        private static readonly Color ArrowColor = new Color(0.95f, 0.08f, 0.06f, 1f);

        private static Mesh s_Mesh;
        private static Material s_Material;
        private static bool s_Warned;

        private MeshRenderer m_Renderer;

        /// <summary>
        /// A hidden arrow, or null if there's no shader to draw it with. It's a scene object of its own rather than a
        /// child of the player, so code that goes through all of a player's renderers (fades, intro, jump shadow)
        /// never picks it up; the ability destroys it with the player.
        /// </summary>
        public static PoleJumpArrow Create()
        {
            if (!MakeResources()) return null;

            var go = new GameObject("PoleJumpArrow");
            go.AddComponent<MeshFilter>().sharedMesh = s_Mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = s_Material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.enabled = false;

            var arrow = go.AddComponent<PoleJumpArrow>();
            arrow.m_Renderer = renderer;
            return arrow;
        }

        /// <summary>Centres the arrow on <paramref name="centre"/>, pointing along <paramref name="direction"/> (world space).</summary>
        public void Show(Vector3 centre, Vector3 direction)
        {
            if (direction.sqrMagnitude < 0.0001f)
            {
                Hide();
                return;
            }
            direction.Normalize();
            // Keep the arrow's "top" (the lit side) facing up as much as it can.
            Vector3 up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            transform.SetPositionAndRotation(centre, Quaternion.LookRotation(direction, up));
            if (!m_Renderer.enabled) m_Renderer.enabled = true;
        }

        public void Hide()
        {
            if (m_Renderer != null && m_Renderer.enabled) m_Renderer.enabled = false;
        }

        // =====================================================================================
        // The shared mesh and material
        // =====================================================================================

        private static bool MakeResources()
        {
            if (s_Material == null)
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    if (!s_Warned) Debug.LogWarning("[PoleJumpArrow] No Sprites/Default shader - no jump arrow on poles.");
                    s_Warned = true;
                    return false;
                }
                s_Material = new Material(shader) { name = "PoleJumpArrow", color = Color.white };
            }
            if (s_Mesh == null) s_Mesh = BuildMesh();
            return true;
        }

        /// <summary>
        /// A shaft and a cone along +Z, centred on the origin, with simple lighting baked into the vertex colours
        /// (lit from above, so it reads as 3D with an unlit shader).
        /// </summary>
        private static Mesh BuildMesh()
        {
            const int sides = 12;
            const float shaftRadius = 0.055f;
            const float headRadius = 0.16f;
            float tail = -Length * 0.5f;
            float neck = Length * 0.05f;
            float tip = Length * 0.5f;

            var vertices = new System.Collections.Generic.List<Vector3>();
            var colors = new System.Collections.Generic.List<Color>();
            var triangles = new System.Collections.Generic.List<int>();
            Vector3 light = new Vector3(0.35f, 1f, -0.25f).normalized;

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                Triangle(a, b, c);
                Triangle(a, c, d);
            }

            void Triangle(Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                float shade = 0.5f + 0.5f * Mathf.Max(0f, Vector3.Dot(normal, light));
                Color color = new Color(ArrowColor.r * shade, ArrowColor.g * shade, ArrowColor.b * shade, 1f);
                int start = vertices.Count;
                vertices.Add(a); vertices.Add(b); vertices.Add(c);
                colors.Add(color); colors.Add(color); colors.Add(color);
                triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
            }

            Vector3 Ring(float radius, float z, int i)
            {
                float angle = i * Mathf.PI * 2f / sides;
                return new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, z);
            }

            for (int i = 0; i < sides; i++)
            {
                int j = i + 1;
                // Shaft side, tail cap, the cone's back, the cone.
                Quad(Ring(shaftRadius, tail, i), Ring(shaftRadius, tail, j), Ring(shaftRadius, neck, j), Ring(shaftRadius, neck, i));
                Triangle(new Vector3(0f, 0f, tail), Ring(shaftRadius, tail, j), Ring(shaftRadius, tail, i));
                Triangle(new Vector3(0f, 0f, neck), Ring(headRadius, neck, j), Ring(headRadius, neck, i));
                Triangle(Ring(headRadius, neck, i), Ring(headRadius, neck, j), new Vector3(0f, 0f, tip));
            }

            var mesh = new Mesh { name = "PoleJumpArrow" };
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
