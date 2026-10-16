using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;
using System.Collections.Generic;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Manages the player's camera rig, handling look input and switching between different camera modes.
    /// This component works with Cinemachine to control the active virtual camera by adjusting priorities.
    /// It should be attached to the player prefab and will only activate for the local owner.
    ///
    /// ZOOM (added 2026-10-16): the mouse wheel, or holding Camera Zoom In / Camera Zoom Out (Change
    /// Keybindings; D-Pad Up / Down and = / - by default), moves the camera closer or further. The range
    /// runs from <see cref="zoomMinDistance"/> (3 m - twice as close as the original 6 m default) out to
    /// wherever puts the camera mode's own Camera Distance (12 m in [BB] FreeLook) at
    /// <see cref="defaultZoomOut"/> (80%) of the way out - 14.25 m with the current numbers - and the camera
    /// starts at that default. Each wheel notch is a fixed fraction of the current distance, so it feels the
    /// same close in and far out. The zoom is kept between rounds (not saved between game launches). It
    /// sets the CinemachineThirdPersonFollow's Camera Distance; the deoccluder still pulls in past walls.
    /// </summary>
    public class CoreCameraController : CoreNetworkBehaviour
    {
        #region Fields & Properties

        [Header("Camera Setup")]
        [Tooltip("List of camera mode prefabs to instantiate. Each should have a CoreCameraMode component.")]
        [SerializeField] private List<CoreCameraMode> cameraModePrefabs = new List<CoreCameraMode>();
        [Tooltip("The transform the camera should follow and look at. If null, this component's transform is used.")]
        [SerializeField] private Transform lookTarget;
        [Tooltip("The name of the camera mode to activate on spawn.")]
        [SerializeField] private string initialModeName = "FreeLook";

        [Header("Default Look Settings")]
        [Tooltip("Whether to process look input from the input handler.")]
        [SerializeField] private bool enableLookInput = true;
        [Tooltip("Default sensitivity for look input (used when camera doesn't override).")]
        [SerializeField] private float defaultLookSensitivity = 1.0f;
        [Tooltip("Default maximum angle in degrees the camera can look up or down (used when camera doesn't override).")]
        [SerializeField] private float defaultVerticalLookLimit = 70.0f;

        [Header("Gamepad Look")]
        [Tooltip("How far the right stick has to be pushed (0-1) before the camera turns - ignores the drift of worn or loose sticks.")]
        [Range(0f, 0.5f)]
        [SerializeField] private float stickDeadzone = 0.1f;
        [Tooltip("Degrees per second the camera turns left/right with the right stick pushed all the way.")]
        [SerializeField] private float stickYawSpeed = 200f;
        [Tooltip("Degrees per second the camera tilts up/down with the right stick pushed all the way.")]
        [SerializeField] private float stickPitchSpeed = 140f;
        [Tooltip("How turn speed follows how far the stick is pushed past the deadzone: 1 = in proportion, higher = slower near the center for finer aiming, full speed at the edge.")]
        [Range(1f, 3f)]
        [SerializeField] private float stickResponseCurve = 1f;

        [Header("Zoom")]
        [Tooltip("Closest the camera can get, in meters.")]
        [SerializeField] private float zoomMinDistance = 3f;
        [Tooltip("How far out the camera mode's own Camera Distance is in the zoom range (0.8 = 80% of the way out). The range's far end is worked out from this.")]
        [Range(0.1f, 1f)]
        [SerializeField] private float defaultZoomOut = 0.8f;
        [Tooltip("Each mouse wheel notch moves the camera this fraction of its current distance closer or further.")]
        [Range(0.01f, 0.5f)]
        [SerializeField] private float zoomPerNotch = 0.1f;
        [Tooltip("Seconds to go all the way from closest to furthest while holding Camera Zoom In / Out.")]
        [SerializeField] private float zoomHoldSeconds = 1.25f;
        [Tooltip("How quickly the camera catches up with the zoom (higher = snappier).")]
        [SerializeField] private float zoomSmoothing = 14f;

        [Header("Listening to Events")]
        [Tooltip("Event that provides look input from the CoreInputHandler.")]
        [SerializeField] private Vector2Event onLookInput;

        /// <summary>
        /// Gets the current horizontal rotation angle of the look target.
        /// </summary>
        public float CurrentHorizontalLookAngle => m_CurrentHorizontalLookAngle;

        /// <summary>
        /// Gets the currently active player rotation coupling mode, determined by the active camera mode.
        /// </summary>
        public CoreMovement.CouplingMode CurrentPlayerRotationMode { get; private set; }

        /// <summary>
        /// Gets the currently active camera mode.
        /// </summary>
        public CoreCameraMode ActiveCameraMode { get; private set; }

        // Internal state for look angles and sensitivity.
        private float m_CurrentVerticalLookAngle;
        private float m_CurrentHorizontalLookAngle;
        private float m_LookSensitivity;
        private float m_VerticalLookLimit;
        private Vector2 m_StickLook;
        private CoreInputHandler m_StickInputSource;

        // Zoom: 0 = closest .. 1 = furthest, kept for the next round's player (-1 = not set yet).
        private static float s_ZoomOut = -1f;
        private float m_ZoomTarget = -1f, m_ZoomDistance;
        private CinemachineThirdPersonFollow m_ZoomFollow;
        private readonly Dictionary<CinemachineThirdPersonFollow, float> m_FollowDefaults = new Dictionary<CinemachineThirdPersonFollow, float>();
        private InputBindingsData m_Bindings;

        // Runtime references.
        private CinemachineBrain m_CinemachineBrain;
        private readonly List<CoreCameraMode> m_RegisteredCameraModes = new List<CoreCameraMode>();
        // Tracks which camera modes were instantiated by this controller and should be destroyed on despawn.
        private readonly HashSet<CoreCameraMode> m_InstantiatedCameraModes = new HashSet<CoreCameraMode>();

        #endregion

        #region Unity & Network Lifecycle

        private void Awake()
        {
            m_LookSensitivity = defaultLookSensitivity;
            m_VerticalLookLimit = defaultVerticalLookLimit;

            if (lookTarget == null)
            {
                lookTarget = transform;
            }

            if(onLookInput == null && enableLookInput)
            {
                Debug.LogError("[CoreCameraController] Look input event is not assigned.", this);
            }

            // Initialize horizontal angle based on the initial rotation of the player object.
            m_CurrentHorizontalLookAngle = transform.rotation.eulerAngles.y;
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            // This component is only active for the local player who owns this object.
            if (IsOwner)
            {
                SetupCinemachine();
                SwitchCameraMode(initialModeName);
                if (enableLookInput)
                {
                    onLookInput.RegisterListener(SetLookInput);

                    m_StickInputSource = GetComponent<CoreInputHandler>();
                    if (m_StickInputSource != null) m_StickInputSource.StickLookChanged += SetStickLookInput;
                }

                m_Bindings = InputBindingsStore.Load();
                InputBindingsStore.Saved += ReloadBindings;
            }
        }

        private void ReloadBindings() => m_Bindings = InputBindingsStore.Load();

        public override void OnNetworkDespawn()
        {
            if (IsOwner)
            {
                // Only destroy camera modes that were instantiated by this controller.
                // External/scene cameras are not destroyed.
                foreach (var mode in m_InstantiatedCameraModes)
                {
                    if (mode != null)
                    {
                        Destroy(mode.gameObject);
                    }
                }
                m_InstantiatedCameraModes.Clear();
                m_RegisteredCameraModes.Clear();

                if (enableLookInput)
                {
                    onLookInput.UnregisterListener(SetLookInput);
                }

                if (m_StickInputSource != null)
                {
                    m_StickInputSource.StickLookChanged -= SetStickLookInput;
                    m_StickInputSource = null;
                }
                m_StickLook = Vector2.zero;
                InputBindingsStore.Saved -= ReloadBindings;
                m_ZoomFollow = null;
                m_FollowDefaults.Clear();
            }
            base.OnNetworkDespawn();
        }

        private void LateUpdate()
        {
            // We update the camera rotation in LateUpdate to ensure all character movement for the frame has been processed.
            // This prevents visual jitter.
            if (lookTarget != null && IsOwner && enableLookInput)
            {
                ApplyStickLook(Time.deltaTime);

                Quaternion horizontalRotation = Quaternion.Euler(0f, m_CurrentHorizontalLookAngle, 0f);
                Quaternion verticalRotation = Quaternion.Euler(m_CurrentVerticalLookAngle, 0f, 0f);
                lookTarget.rotation = horizontalRotation * verticalRotation;
            }

            if (IsOwner) UpdateZoom(Time.unscaledDeltaTime);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Sets the look input, updating the horizontal and vertical look angles.
        /// </summary>
        /// <param name="lookInput">The 2D vector from the input device.</param>
        public void SetLookInput(Vector2 lookInput)
        {
            if (!IsOwner || !enableLookInput) return;

            m_CurrentHorizontalLookAngle += lookInput.x * m_LookSensitivity;

            // Clamp the vertical angle to prevent the camera from flipping over.
            m_CurrentVerticalLookAngle = Mathf.Clamp(m_CurrentVerticalLookAngle - (lookInput.y * m_LookSensitivity), -m_VerticalLookLimit, m_VerticalLookLimit);
        }

        /// <summary>
        /// Sets the right stick's current position (from CoreInputHandler.StickLookChanged). The camera keeps
        /// turning every frame while the stick is held past the deadzone - see <see cref="ApplyStickLook"/>.
        /// </summary>
        public void SetStickLookInput(Vector2 stick)
        {
            m_StickLook = stick;
        }

        /// <summary>
        /// Turns the camera for one frame from the held right stick: nothing inside the deadzone, then a
        /// speed that grows with how far the stick is pushed (rescaled so it starts from zero just past the
        /// deadzone, shaped by stickResponseCurve), in the direction the stick points. Independent of the
        /// mouse sensitivity, which is tuned for mouse deltas.
        /// </summary>
        private void ApplyStickLook(float deltaTime)
        {
            if (!Application.isFocused) return; // a pad reaches background windows too (see CoreInputHandler)

            float magnitude = Mathf.Min(1f, m_StickLook.magnitude);
            if (magnitude <= stickDeadzone) return;

            float amount = Mathf.Clamp01((magnitude - stickDeadzone) / Mathf.Max(0.0001f, 1f - stickDeadzone));
            amount = Mathf.Pow(amount, stickResponseCurve);
            Vector2 direction = m_StickLook / m_StickLook.magnitude;

            m_CurrentHorizontalLookAngle += direction.x * amount * stickYawSpeed * deltaTime;
            m_CurrentVerticalLookAngle = Mathf.Clamp(
                m_CurrentVerticalLookAngle - direction.y * amount * stickPitchSpeed * deltaTime,
                -m_VerticalLookLimit, m_VerticalLookLimit);
        }

        /// <summary>
        /// Sets the look sensitivity at runtime.
        /// </summary>
        /// <param name="sensitivity">The new sensitivity value.</param>
        /// <summary>Points the camera straight ahead along <paramref name="yaw"/> (used when the player is teleported, e.g. Practice Challenges' Retry).</summary>
        public void ResetLook(float yaw)
        {
            m_CurrentHorizontalLookAngle = yaw;
            m_CurrentVerticalLookAngle = 0f;
        }

        public void SetLookSensitivity(float sensitivity)
        {
            m_LookSensitivity = sensitivity;
        }

        /// <summary>
        /// Switches the active camera mode by changing Cinemachine virtual camera priorities.
        /// </summary>
        /// <param name="modeName">The name of the camera mode to activate.</param>
        /// <returns>True if the mode was found and switched successfully, false otherwise.</returns>
        public bool SwitchCameraMode(string modeName)
        {
            CoreCameraMode targetMode = m_RegisteredCameraModes.Find(m => m.ModeName == modeName);
            if (targetMode == null)
            {
                Debug.LogWarning($"Camera mode '{modeName}' not found.");
                return false;
            }

            if (targetMode.CinemachineCamera == null)
            {
                Debug.LogWarning($"CinemachineCamera for mode '{modeName}' is not available.");
                return false;
            }

            // Update the active camera mode reference.
            ActiveCameraMode = targetMode;

            // Update the current player rotation mode.
            CurrentPlayerRotationMode = targetMode.PlayerRotationMode;

            // Apply look settings from the camera mode or use defaults.
            if (targetMode.OverrideLookSettings)
            {
                m_LookSensitivity = targetMode.LookSensitivity;
                m_VerticalLookLimit = targetMode.VerticalLookLimit;
            }
            else
            {
                m_LookSensitivity = defaultLookSensitivity;
                m_VerticalLookLimit = defaultVerticalLookLimit;
            }

            // Set priorities: the target camera gets active priority, all others get inactive.
            foreach (var mode in m_RegisteredCameraModes)
            {
                if (mode != null)
                {
                    mode.SetActive(mode == targetMode);
                }
            }

            return true;
        }

        /// <summary>
        /// Registers a camera mode at runtime by instantiating from a prefab.
        /// The instantiated camera will be destroyed when the player despawns.
        /// For scene cameras that should not be destroyed, use RegisterCameraModeInstance instead.
        /// </summary>
        /// <param name="cameraModePrefab">The camera mode prefab to instantiate and register.</param>
        /// <returns>The registered CoreCameraMode instance, or null if registration failed.</returns>
        public CoreCameraMode RegisterCameraMode(CoreCameraMode cameraModePrefab)
        {
            if (cameraModePrefab == null)
            {
                Debug.LogWarning("[CoreCameraController] Cannot register a null camera mode prefab.", this);
                return null;
            }

            // Validate no duplicate mode names exist to prevent ambiguous SwitchCameraMode calls.
            if (m_RegisteredCameraModes.Exists(m => m.ModeName == cameraModePrefab.ModeName))
            {
                Debug.LogWarning($"[CoreCameraController] A camera mode with name '{cameraModePrefab.ModeName}' is already registered. Duplicate names are not allowed.", this);
                return null;
            }

            // Always instantiate the prefab to avoid modifying the original asset.
            CoreCameraMode modeToRegister = Instantiate(cameraModePrefab);

            // Set up targets and add to the registered list.
            modeToRegister.SetTargets(lookTarget);
            m_RegisteredCameraModes.Add(modeToRegister);
            m_InstantiatedCameraModes.Add(modeToRegister);

            // Ensure the newly registered mode is inactive by default.
            modeToRegister.SetActive(false);

            return modeToRegister;
        }

        /// <summary>
        /// Registers an existing in-scene camera mode instance. Use this for cameras already placed in the scene.
        /// </summary>
        /// <param name="cameraModeInstance">The camera mode instance to register.</param>
        /// <returns>True if registration was successful, false otherwise.</returns>
        public bool RegisterCameraModeInstance(CoreCameraMode cameraModeInstance)
        {
            if (cameraModeInstance == null)
            {
                Debug.LogWarning("[CoreCameraController] Cannot register a null camera mode instance.", this);
                return false;
            }

            if (m_RegisteredCameraModes.Contains(cameraModeInstance))
            {
                Debug.LogWarning($"[CoreCameraController] Camera mode instance '{cameraModeInstance.ModeName}' is already registered.", this);
                return false;
            }

            // Validate no duplicate mode names exist to prevent ambiguous SwitchCameraMode calls.
            if (m_RegisteredCameraModes.Exists(m => m.ModeName == cameraModeInstance.ModeName))
            {
                Debug.LogWarning($"[CoreCameraController] A camera mode with name '{cameraModeInstance.ModeName}' is already registered. Duplicate names are not allowed.", this);
                return false;
            }

            // Set up targets and add to the registered list.
            cameraModeInstance.SetTargets(lookTarget);
            m_RegisteredCameraModes.Add(cameraModeInstance);

            // Ensure the newly registered mode is inactive by default.
            cameraModeInstance.SetActive(false);

            return true;
        }

        /// <summary>
        /// Unregisters a camera mode at runtime.
        /// </summary>
        /// <param name="cameraMode">The camera mode to unregister.</param>
        /// <param name="destroy">If true, destroys the camera mode GameObject. If false, only removes from the list.</param>
        /// <returns>True if the mode was found and unregistered, false otherwise.</returns>
        public bool UnregisterCameraMode(CoreCameraMode cameraMode, bool destroy = false)
        {
            if (cameraMode == null)
            {
                Debug.LogWarning("[CoreCameraController] Cannot unregister a null camera mode.", this);
                return false;
            }

            if (!m_RegisteredCameraModes.Contains(cameraMode))
            {
                Debug.LogWarning($"[CoreCameraController] Camera mode '{cameraMode.ModeName}' is not registered.", this);
                return false;
            }

            // If this was the active camera mode, we need to switch to another one.
            if (ActiveCameraMode == cameraMode)
            {
                // Find another mode to switch to before clearing the active mode.
                CoreCameraMode fallbackMode = null;
                foreach (var mode in m_RegisteredCameraModes)
                {
                    if (mode != cameraMode && mode != null)
                    {
                        fallbackMode = mode;
                        break;
                    }
                }

                // Switch to the fallback mode if found, otherwise clear the active mode.
                if (fallbackMode != null)
                {
                    SwitchCameraMode(fallbackMode.ModeName);
                }
                else
                {
                    ActiveCameraMode = null;
                    CurrentPlayerRotationMode = CoreMovement.CouplingMode.Decoupled;
                }
            }

            m_RegisteredCameraModes.Remove(cameraMode);
            m_InstantiatedCameraModes.Remove(cameraMode);

            if (destroy && cameraMode != null)
            {
                Destroy(cameraMode.gameObject);
            }

            return true;
        }

        /// <summary>
        /// Unregisters a camera mode by name.
        /// </summary>
        /// <param name="modeName">The name of the camera mode to unregister.</param>
        /// <param name="destroy">If true, destroys the camera mode GameObject. If false, only removes from the list.</param>
        /// <returns>True if the mode was found and unregistered, false otherwise.</returns>
        public bool UnregisterCameraMode(string modeName, bool destroy = false)
        {
            CoreCameraMode targetMode = m_RegisteredCameraModes.Find(m => m.ModeName == modeName);
            if (targetMode == null)
            {
                Debug.LogWarning($"[CoreCameraController] Camera mode '{modeName}' not found for unregistration.", this);
                return false;
            }

            return UnregisterCameraMode(targetMode, destroy);
        }

        /// <summary>
        /// Gets all currently registered camera modes.
        /// </summary>
        /// <returns>A read-only list of registered camera modes.</returns>
        public IReadOnlyList<CoreCameraMode> GetRegisteredCameraModes()
        {
            return m_RegisteredCameraModes.AsReadOnly();
        }

        /// <summary>
        /// Checks if a camera mode with the specified name is registered.
        /// </summary>
        /// <param name="modeName">The name of the camera mode to check.</param>
        /// <returns>True if registered, false otherwise.</returns>
        public bool IsCameraModeRegistered(string modeName)
        {
            return m_RegisteredCameraModes.Exists(m => m.ModeName == modeName);
        }

        #endregion

        #region Zoom

        /// <summary>Mouse wheel / held zoom buttons -> the active camera mode's Camera Distance (see the class summary).</summary>
        private void UpdateZoom(float deltaTime)
        {
            CinemachineThirdPersonFollow follow = ActiveCameraMode != null && ActiveCameraMode.CinemachineCamera != null
                ? ActiveCameraMode.CinemachineCamera.GetComponent<CinemachineThirdPersonFollow>()
                : null;
            if (follow == null) return;

            // The mode's own distance (before we ever changed it) is the default, 80% of the way out.
            if (!m_FollowDefaults.TryGetValue(follow, out float standard))
            {
                standard = Mathf.Max(0.1f, follow.CameraDistance);
                m_FollowDefaults[follow] = standard;
            }
            float min = Mathf.Min(zoomMinDistance, standard);
            float max = min + (standard - min) / Mathf.Max(0.01f, defaultZoomOut);

            if (follow != m_ZoomFollow || m_ZoomTarget < 0f)
            {
                m_ZoomFollow = follow;
                m_ZoomTarget = s_ZoomOut >= 0f ? Mathf.Lerp(min, max, s_ZoomOut) : standard;
                m_ZoomDistance = m_ZoomTarget;
            }

            if (CanZoom())
            {
                float notches = ScrollNotches();
                if (notches != 0f) m_ZoomTarget *= Mathf.Pow(1f - zoomPerNotch, notches); // wheel up = closer

                float hold = 0f;
                if (m_Bindings != null)
                {
                    if (BoundControls.IsHeld(m_Bindings.Keyboard.CameraZoomIn, m_Bindings.Gamepad.CameraZoomIn)) hold -= 1f;
                    if (BoundControls.IsHeld(m_Bindings.Keyboard.CameraZoomOut, m_Bindings.Gamepad.CameraZoomOut)) hold += 1f;
                }
                if (hold != 0f && max > min)
                {
                    float rate = Mathf.Log(max / min) / Mathf.Max(0.1f, zoomHoldSeconds); // whole range in zoomHoldSeconds
                    m_ZoomTarget *= Mathf.Exp(hold * rate * deltaTime);
                }
            }

            m_ZoomTarget = Mathf.Clamp(m_ZoomTarget, min, max);
            s_ZoomOut = max > min ? Mathf.InverseLerp(min, max, m_ZoomTarget) : 1f;
            m_ZoomDistance += (m_ZoomTarget - m_ZoomDistance) * (1f - Mathf.Exp(-zoomSmoothing * deltaTime));
            follow.CameraDistance = m_ZoomDistance;
        }

        /// <summary>Not while a menu has the controls (Esc menu, betting pause) or the intro owns the camera.</summary>
        private static bool CanZoom()
        {
            if (!Application.isFocused) return false;
            if (CoreInputHandler.GameplayInputBlocked || InGameMenu.IsOpen || SettingsOverlay.IsOpen) return false;
            if (MatchIntro.IsActive) return false;
            return true;
        }

        /// <summary>Mouse wheel notches this frame (+ = away from you / up), whichever scroll scale the Input System uses.</summary>
        private static float ScrollNotches()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return 0f;
            float y = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(y) < 0.0001f) return 0f;

            // KeepPlatformSpecificInputRange reports 120 per notch on Windows; UniformAcrossAllPlatforms reports about 1.
            bool raw = InputSystem.settings != null &&
                       InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange;
            return Mathf.Clamp(raw ? y / 120f : y, -5f, 5f);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Initializes the Cinemachine setup by finding the brain, instantiating camera prefabs,
        /// and linking all camera modes to their targets.
        /// </summary>
        private void SetupCinemachine()
        {
            var mainCam = Camera.main;
            if (mainCam == null)
            {
                Debug.LogError("No main camera found in the scene. [CoreCameraController] requires a main camera.", this);
                return;
            }

            // Ensure a CinemachineBrain exists on the main camera.
            m_CinemachineBrain = mainCam.GetComponent<CinemachineBrain>();
            if (m_CinemachineBrain == null)
            {
                m_CinemachineBrain = mainCam.gameObject.AddComponent<CinemachineBrain>();
            }

            if (cameraModePrefabs.Count == 0)
            {
                Debug.LogError("[CoreCameraController] No camera mode prefabs assigned.", this);
                return;
            }

            // Instantiate each camera mode prefab and set up targets.
            foreach (var prefab in cameraModePrefabs)
            {
                if (prefab == null)
                {
                    Debug.LogWarning("Null camera mode prefab in [CoreCameraController]", this);
                    continue;
                }

                // Validate no duplicate mode names exist to prevent ambiguous SwitchCameraMode calls.
                if (m_RegisteredCameraModes.Exists(m => m.ModeName == prefab.ModeName))
                {
                    Debug.LogWarning($"[CoreCameraController] Duplicate camera mode name '{prefab.ModeName}' found in prefab list. Skipping duplicate.", this);
                    continue;
                }

                CoreCameraMode instance = Instantiate(prefab);
                instance.SetTargets(lookTarget);
                m_RegisteredCameraModes.Add(instance);
                m_InstantiatedCameraModes.Add(instance);
            }
        }

        #endregion
    }
}
