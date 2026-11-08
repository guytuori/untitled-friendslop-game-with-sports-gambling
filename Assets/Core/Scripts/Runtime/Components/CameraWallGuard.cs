using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.Rendering;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Keeps the gameplay camera on the player's side of walls (2026-11-08). Added at runtime by
    /// CoreCameraController to each camera mode it makes; runs last in the Cinemachine pipeline, after the
    /// Deoccluder.
    ///
    /// Why it's needed: the Deoccluder ignores anything closer to the look target than its Minimum Distance From
    /// Target (0.3 m) - but the player's capsule is only 0.29 m thick, so with your back to a wall the wall was
    /// ignored and the camera went through to the other side (or out of the map, in the zoo's tunnels).
    /// Lowering that setting made things worse (the camera got pulled into the character's head far more often),
    /// so it stays as it was and this does one extra check instead:
    ///   - a sphere cast (<see cref="Radius"/>) from the look target to where the camera ended up, against level
    ///     geometry (Default and Ground layers - not players, rails or poles); if it's blocked, the camera stops
    ///     just this side of it. One cast a frame.
    ///   - the near clip plane is kept small enough that the corners of the view can't reach past that radius at
    ///     the current field of view, so a camera pressed up to a wall doesn't show through it.
    ///   - when the camera ends up closer than <see cref="HideCharacterDistance"/> to the look target (inside the
    ///     character), the local player's own model stops drawing (it still casts its shadow) until it moves out.
    /// </summary>
    [AddComponentMenu("")] // added by code
    public class CameraWallGuard : CinemachineExtension
    {
        /// <summary>How far (m) the camera is kept from level geometry.</summary>
        public const float Radius = 0.2f;

        /// <summary>The character stops drawing when the camera is closer than this (m) to the look target.</summary>
        public const float HideCharacterDistance = 0.55f;

        /// <summary>The largest near clip plane used (the camera's usual one).</summary>
        private const float MaxNearClip = 0.3f;

        private static int s_Mask = -1;

        private readonly Dictionary<Renderer, ShadowCastingMode> m_Hidden = new Dictionary<Renderer, ShadowCastingMode>();
        private float m_NextRendererRefresh;

        private static int Mask
        {
            get
            {
                if (s_Mask == -1)
                {
                    s_Mask = LayerMask.GetMask("Default", "Ground");
                    if (s_Mask == 0) s_Mask = 1; // Default
                }
                return s_Mask;
            }
        }

        protected override void PostPipelineStageCallback(CinemachineVirtualCameraBase vcam, CinemachineCore.Stage stage, ref CameraState state, float deltaTime)
        {
            if (stage != CinemachineCore.Stage.Finalize) return;

            Transform target = vcam.LookAt != null ? vcam.LookAt : vcam.Follow;
            if (target == null) return;

            // Near clip: the view's corners reach near * cornerFactor from the camera; keep that inside Radius.
            LensSettings lens = state.Lens;
            Camera main = Camera.main;
            float aspect = Mathf.Max(lens.Aspect, main != null ? main.aspect : 0f); // the wider, to be safe
            float near = NearClipFor(lens.FieldOfView, aspect);
            if (!Mathf.Approximately(lens.NearClipPlane, near))
            {
                lens.NearClipPlane = near;
                state.Lens = lens;
            }

            Vector3 origin = target.position;
            Vector3 camera = state.GetCorrectedPosition();
            Vector3 toCamera = camera - origin;
            float distance = toCamera.magnitude;

            if (distance > 0.001f)
            {
                Vector3 direction = toCamera / distance;
                if (Physics.SphereCast(origin, Radius, direction, out RaycastHit hit, distance, Mask, QueryTriggerInteraction.Ignore))
                {
                    Vector3 safe = origin + direction * hit.distance;
                    state.PositionCorrection += safe - camera;
                    distance = hit.distance;
                }
            }

            HideCharacter(target, distance < HideCharacterDistance);
        }

        private static float NearClipFor(float verticalFov, float aspect)
        {
            float tanV = Mathf.Tan(Mathf.Clamp(verticalFov, 1f, 179f) * 0.5f * Mathf.Deg2Rad);
            float tanH = tanV * Mathf.Max(0.1f, aspect);
            float cornerFactor = Mathf.Sqrt(1f + tanV * tanV + tanH * tanH);
            return Mathf.Clamp(Radius * 0.9f / cornerFactor, 0.01f, MaxNearClip);
        }

        /// <summary>The local player's model: shadow only while the camera is inside it, back to normal after.</summary>
        private void HideCharacter(Transform target, bool hide)
        {
            if (!hide)
            {
                RestoreCharacter();
                return;
            }

            // Gather (again now and then - a character swap makes new renderers) whatever is drawing normally.
            if (m_Hidden.Count == 0 || Time.unscaledTime >= m_NextRendererRefresh)
            {
                m_NextRendererRefresh = Time.unscaledTime + 0.5f;
                CorePlayerManager player = target.GetComponentInParent<CorePlayerManager>();
                if (player == null) return;
                foreach (Renderer r in player.GetComponentsInChildren<Renderer>())
                {
                    if (r == null || m_Hidden.ContainsKey(r) || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                    if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                    m_Hidden[r] = r.shadowCastingMode;
                    r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                }
            }
        }

        private void RestoreCharacter()
        {
            if (m_Hidden.Count == 0) return;
            foreach (KeyValuePair<Renderer, ShadowCastingMode> entry in m_Hidden)
            {
                if (entry.Key != null) entry.Key.shadowCastingMode = entry.Value;
            }
            m_Hidden.Clear();
        }

        protected override void OnDestroy()
        {
            RestoreCharacter();
            base.OnDestroy();
        }

        private void OnDisable() => RestoreCharacter();
    }
}
