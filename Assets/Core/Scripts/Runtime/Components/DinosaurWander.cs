using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Walks a scenery dinosaur around near where it was placed in the map: stand around for a few seconds,
    /// pick a direction that's clear, walk a bit, repeat. Added by <see cref="Dinosaurs"/> when the map is
    /// built; nothing to set up by hand.
    ///
    /// Before each step it checks several points under the body (nose end, both front corners, the middle,
    /// the back corners) at the next position: none may hang over a drop of more than <see cref="MaxStep"/>
    /// (edges, pits), the middle (the legs) can't step up more than that either, none may be in water or a
    /// challenge hole, and there mustn't be a wall or fence in the way of the legs (thin things like posts
    /// and trees are walked past). Otherwise it stops and picks a new direction. It heads back when it gets
    /// more than <see cref="m_WanderRadius"/> from home. Gates and gaps are fair game, so a dinosaur can
    /// leave its pen if the pen is open.
    ///
    /// Safety net: if it's ever over nothing for a second, in water, in a challenge hole or well below
    /// where it started, it's put straight back where it was placed.
    ///
    /// Animation: the model's idle and walk clips (from the <see cref="DinosaurCatalog"/>), blended with a
    /// PlayableGraph on its own Animator - no controller asset needed. The walk speed is measured from the
    /// walk clip itself (how far a planted foot travels per cycle) so the feet don't slide.
    ///
    /// Habits - every 30-45 s (<see cref="HabitMinSeconds"/>-<see cref="HabitMaxSeconds"/>) it stops for a moment:
    ///   - plant eaters graze: the neck and head bend down until the snout is near the ground (worked out
    ///     from the bones each time, so it suits each species and slope), it tugs at the grass with a few
    ///     shakes of the head, chews, and looks up again. There's no eating clip in the pack, so this is
    ///     done on top of the idle animation, in LateUpdate;
    ///   - the T-rex plays its Jump clip and roars (dinosaur.mp3) if this machine's player is within
    ///     <see cref="RoarRange"/> - first turning on the spot to face that player (shuffling its feet);
    ///   - the raptors play their Attack clip (sometimes twice).
    /// Which one is decided by the model's name (<see cref="HabitFor"/>).
    /// </summary>
    public class DinosaurWander : MonoBehaviour
    {
        /// <summary>Most height change (meters) between a point under the body now and one step later.</summary>
        public const float MaxStep = 0.75f;

        private const float ProbeInterval = 0.1f;
        private const float ProbeUp = 1.5f;       // ground probes start this far above the expected ground
        private const float LostGroundSeconds = 1f;
        private const float FallLimit = 20f;     // more than this below home = fell off the map

        private static readonly RaycastHit[] s_Hits = new RaycastHit[16];
        private static readonly Vector2[] s_Candidates = new Vector2[16]; // (yaw, preference) for TryStartWalk

        /// <summary>Seconds between habits (grazing, the T-rex's roar, the raptors' lunge).</summary>
        public const float HabitMinSeconds = 30f, HabitMaxSeconds = 45f;

        /// <summary>The T-rex only roars when this machine's player is within this many meters of it.</summary>
        public const float RoarRange = 40f;

        private const float GrazeSeconds = 8f;       // head down, tug at the grass, chew, head up
        private const float GrazeLower = 1.6f;       // seconds to lower (and to raise) the head
        private const float MaxGrazePitch = 100f;    // degrees, shared by shoulders / neck / head
        private const float MaxHeadDown = 80f;       // the head never points further down than this (no curling under)
        private const float UprightLean = 45f;       // two-legged plant eaters lean the upper body forward this much
        private const float ShouldersShare = 0.12f, NeckShare = 0.5f, HeadShare = 0.38f;
        private const float ShakeDegrees = 14f;
        private const float RoarDelay = 0.15f;       // into the jump
        private const float FaceTurnRate = 60f;      // degrees per second, turning to face the player before a roar
        private const float FaceTimeout = 4f;        // give up turning (and roar anyway) after this long

        private enum State { Idle, Walk, Act }

        /// <summary>What a dinosaur does now and then: plant eaters graze, the T-rex stomps and roars, raptors lunge.</summary>
        public enum Habit { Graze, Stomp, Lunge }

        private int m_Index;
        private System.Random m_Random;

        // Home (where it was placed) and the model's own axes.
        private Vector3 m_HomePosition;
        private float m_HomeYaw;
        private Vector3 m_ModelForward;          // the model's facing, in its own space (flat)
        private Quaternion m_Tilt;               // its rotation relative to its facing (kept as placed)
        private float m_HeightOffset;            // placed height above the ground under it
        private bool m_HomeIsValid = true;

        // Size (meters): body extent ahead of / behind its origin, half width, height.
        private float m_Front, m_Back, m_HalfWidth, m_Height;
        private float m_WanderRadius;

        // Movement.
        private State m_State;
        private float m_StateTimer;
        private float m_Yaw, m_TargetYaw;
        private float m_WalkLeft;
        private float m_Speed;                   // walking speed (matches the walk clip)
        private float m_CurrentSpeed;
        private float m_TurnRate;                // degrees per second
        private bool m_Blocked;
        private float m_GroundY;
        private float m_NextProbe;
        private float m_NoGroundTime;

        // Animation.
        private Animator m_Animator;
        private PlayableGraph m_Graph;
        private AnimationMixerPlayable m_Mixer;
        private AnimationClipPlayable m_IdlePlayable, m_WalkPlayable;
        private AnimationClip m_IdleClip, m_WalkClip;
        private double m_IdleTime, m_WalkTime;
        private float m_WalkWeight;

        // Habits.
        private Habit m_Habit;
        private float m_NextHabitAt;
        private float m_ActTime, m_ActLength;
        private AnimationClip m_ActClip;                 // the T-rex's jump / the raptor's attack
        private AnimationClipPlayable m_ActPlayable;
        private bool m_Roared;
        private bool m_Facing;                           // the T-rex is turning to face the player first
        private float m_FaceTimeLeft;
        private int m_Tugs;
        private float m_RestoreCullingAt = -1f;

        // Grazing: the bones that bend (upper body, shoulders, neck, head - any may be missing), the snout tip.
        private Transform[] m_BendBones;
        private readonly Quaternion[] m_BendBase = new Quaternion[4];
        private readonly Quaternion[] m_BendWritten = new Quaternion[4];
        private bool m_BendApplied;
        private Transform m_Snout;
        private Vector3 m_SnoutLocal;                    // in the head's space, if there's no Head_end bone
        private float m_GrazePitch, m_GrazeTorso;
        private bool m_Upright;                          // stands on two legs (shoulders above the torso), e.g. Parasaurolophus

        private Renderer[] m_Renderers;
        private AudioSource m_Voice;

        public int Index => m_Index;
        public Habit Kind => m_Habit;

        /// <summary>True while its Interact sound is playing.</summary>
        public bool IsMakingSound => m_Voice != null && m_Voice.isPlaying;

        // =====================================================================================
        // Set-up
        // =====================================================================================

        public void Init(int index, DinosaurCatalog.Entry entry)
        {
            m_Index = index;
            m_IdleClip = entry.idle;
            m_WalkClip = entry.walk;
            m_Habit = HabitFor(entry.id);
            m_ActClip = m_Habit == Habit.Stomp ? entry.jump : m_Habit == Habit.Lunge ? entry.attack : null;
            m_Random = new System.Random(index * 7919 + 17);
            m_Renderers = GetComponentsInChildren<Renderer>(true);

            // Which way the model faces: towards its head bone, if it has one.
            m_ModelForward = Vector3.forward;
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (!string.Equals(t.name, "Head", System.StringComparison.OrdinalIgnoreCase)) continue;
                Vector3 local = transform.InverseTransformPoint(t.position);
                local.y = 0f;
                if (local.sqrMagnitude > 0.0001f) m_ModelForward = local.normalized;
                break;
            }

            Vector3 heading = Flat(transform.rotation * m_ModelForward);
            if (heading.sqrMagnitude < 0.0001f) heading = Vector3.forward;
            m_HomeYaw = YawOf(heading);
            m_Yaw = m_TargetYaw = m_HomeYaw;
            m_Tilt = Quaternion.Inverse(Quaternion.LookRotation(heading)) * transform.rotation;
            m_HomePosition = transform.position;

            MeasureBody(heading);
            if (m_Habit == Habit.Graze) FindBendBones();
            float length = m_Front + m_Back;
            m_WanderRadius = Mathf.Max(20f, length * 4f);
            m_TurnRate = Mathf.Clamp(900f / Mathf.Max(1f, length), 20f, 120f);

            if (TryGround(m_HomePosition, m_HomePosition.y, out float ground))
            {
                m_GroundY = ground;
                m_HeightOffset = Mathf.Clamp(m_HomePosition.y - ground, -1f, 1f);
            }
            else
            {
                m_GroundY = m_HomePosition.y;
                m_HeightOffset = 0f;
            }

            m_HomeIsValid = !Dinosaurs.IsForbidden(m_HomePosition) && !WaterVolume.IsInWater(new Vector3(m_HomePosition.x, m_GroundY + 0.2f, m_HomePosition.z));
            if (!m_HomeIsValid)
            {
                Debug.LogWarning($"[Dinosaurs] '{name}' was placed in water or a challenge hole - it'll stay put.", this);
            }

            m_Animator = GetComponent<Animator>();
            if (m_Animator == null) m_Animator = gameObject.AddComponent<Animator>();
            m_Animator.applyRootMotion = false;
            m_Animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

            m_Speed = MeasureWalkSpeed(heading);
            if (m_Speed <= 0f) m_Speed = Mathf.Clamp(length * 0.15f, 1f, 6f);
            BuildGraph();

            m_Voice = gameObject.AddComponent<AudioSource>();
            m_Voice.playOnAwake = false;
            m_Voice.spatialBlend = 1f;
            m_Voice.rolloffMode = AudioRolloffMode.Logarithmic;
            m_Voice.minDistance = Mathf.Max(6f, length * 0.5f);
            m_Voice.maxDistance = 200f;
            m_Voice.dopplerLevel = 0f;

            // Don't all start (and probe) in step.
            m_IdleTime = m_Random.NextDouble() * 10.0;
            StartIdle(Range(0.5f, 4f));
            m_NextProbe = Time.time + (float)m_Random.NextDouble() * ProbeInterval;

            // The first habit comes a little sooner, so there's something to see early in the round.
            m_NextHabitAt = Time.time + Range(8f, HabitMaxSeconds);
        }

        /// <summary>Plant eaters graze; the T-rex stomps and roars; raptors lunge. Decided by the model's name.</summary>
        public static Habit HabitFor(string id)
        {
            string name = id ?? string.Empty;
            if (name.IndexOf("rex", System.StringComparison.OrdinalIgnoreCase) >= 0) return Habit.Stomp;
            if (name.IndexOf("raptor", System.StringComparison.OrdinalIgnoreCase) >= 0) return Habit.Lunge;
            return Habit.Graze;
        }

        /// <summary>The spine bones that bend to graze, and the snout tip (Quaternius rigs: Torso > Shoulders > Neck > Head > Head_end).</summary>
        private void FindBendBones()
        {
            Transform torso = null, shoulders = null, neck = null, head = null, headEnd = null;
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (Is(t, "Torso")) torso = t;
                else if (Is(t, "Shoulders")) shoulders = t;
                else if (Is(t, "Neck")) neck = t;
                else if (Is(t, "Head")) head = t;
                else if (Is(t, "Head_end")) headEnd = t;
            }
            if (head == null && neck == null) return;

            m_BendBones = new[] { torso, shoulders, neck, head };
            m_Upright = torso != null && shoulders != null && shoulders.position.y > torso.position.y;
            if (headEnd != null && head != null && headEnd.IsChildOf(head))
            {
                m_Snout = headEnd;
            }
            else if (head != null)
            {
                // No end bone: guess the snout as far beyond the head as the head is from the neck.
                m_Snout = head;
                Vector3 along = neck != null ? head.position - neck.position : transform.rotation * m_ModelForward;
                m_SnoutLocal = head.InverseTransformVector(along);
            }

            static bool Is(Transform t, string name) => string.Equals(t.name, name, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Body extent from the meshes at rest, measured along the facing and across it.</summary>
        private void MeasureBody(Vector3 heading)
        {
            Vector3 right = Vector3.Cross(Vector3.up, heading);
            Vector3 origin = transform.position;
            float front = 0f, back = 0f, half = 0f, height = 0f;
            bool any = false;

            foreach (Renderer r in m_Renderers)
            {
                Mesh mesh = r is SkinnedMeshRenderer skin ? skin.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                Bounds b = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 d = r.transform.TransformPoint(corner) - origin;
                    front = Mathf.Max(front, Vector3.Dot(d, heading));
                    back = Mathf.Max(back, -Vector3.Dot(d, heading));
                    half = Mathf.Max(half, Mathf.Abs(Vector3.Dot(d, right)));
                    height = Mathf.Max(height, d.y);
                    any = true;
                }
            }

            m_Front = any ? Mathf.Max(0.5f, front) : 1.5f;
            m_Back = any ? Mathf.Max(0.5f, back) : 1.5f;
            m_HalfWidth = any ? Mathf.Max(0.3f, half) : 0.75f;
            m_Height = any ? Mathf.Max(0.5f, height) : 2f;
        }

        /// <summary>
        /// Meters per second the walk clip moves at: the furthest a low (foot) bone travels along the facing in
        /// one cycle is a stride, covered while the foot is planted (about 60% of the cycle).
        /// </summary>
        private float MeasureWalkSpeed(Vector3 heading)
        {
            if (m_WalkClip == null || m_WalkClip.length <= 0.01f) return 0f;

            Vector3 origin = transform.position;
            var feet = new System.Collections.Generic.List<Transform>();
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t.childCount == 0 && t.GetComponent<Renderer>() == null && t.position.y - origin.y < m_Height * 0.15f) feet.Add(t);
            }
            if (feet.Count == 0) return 0f;

            var min = new float[feet.Count];
            var max = new float[feet.Count];
            for (int f = 0; f < feet.Count; f++) { min[f] = float.MaxValue; max[f] = float.MinValue; }

            const int samples = 40;
            for (int s = 0; s < samples; s++)
            {
                m_WalkClip.SampleAnimation(gameObject, m_WalkClip.length * s / samples);
                for (int f = 0; f < feet.Count; f++)
                {
                    float along = Vector3.Dot(feet[f].position - origin, heading);
                    min[f] = Mathf.Min(min[f], along);
                    max[f] = Mathf.Max(max[f], along);
                }
            }
            if (m_IdleClip != null) m_IdleClip.SampleAnimation(gameObject, 0f);

            float stride = 0f;
            for (int f = 0; f < feet.Count; f++) stride = Mathf.Max(stride, max[f] - min[f]);
            float speed = stride / (m_WalkClip.length * 0.6f);
            return speed > 0.2f ? Mathf.Clamp(speed, 0.5f, 15f) : 0f;
        }

        private void BuildGraph()
        {
            if (m_IdleClip == null && m_WalkClip == null) return;

            m_Graph = PlayableGraph.Create($"Dinosaur {name}");
            m_Graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            var output = AnimationPlayableOutput.Create(m_Graph, "Dinosaur", m_Animator);
            m_Mixer = AnimationMixerPlayable.Create(m_Graph, 3);
            output.SetSourcePlayable(m_Mixer);

            if (m_IdleClip != null)
            {
                m_IdlePlayable = AnimationClipPlayable.Create(m_Graph, m_IdleClip);
                m_IdlePlayable.SetApplyFootIK(false);
                m_IdlePlayable.SetSpeed(0); // time is set by hand, so non-looping clips loop too
                m_Graph.Connect(m_IdlePlayable, 0, m_Mixer, 0);
            }
            if (m_WalkClip != null)
            {
                m_WalkPlayable = AnimationClipPlayable.Create(m_Graph, m_WalkClip);
                m_WalkPlayable.SetApplyFootIK(false);
                m_WalkPlayable.SetSpeed(0);
                m_Graph.Connect(m_WalkPlayable, 0, m_Mixer, 1);
            }
            if (m_ActClip != null)
            {
                m_ActPlayable = AnimationClipPlayable.Create(m_Graph, m_ActClip);
                m_ActPlayable.SetApplyFootIK(false);
                m_ActPlayable.SetSpeed(0);
                m_Graph.Connect(m_ActPlayable, 0, m_Mixer, 2);
            }

            UpdateAnimation(0f);
            m_Graph.Play();
        }

        private void OnDestroy()
        {
            if (m_Graph.IsValid()) m_Graph.Destroy();
        }

        // =====================================================================================
        // Behaviour
        // =====================================================================================

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            if (Time.time >= m_NextProbe)
            {
                m_NextProbe = Time.time + ProbeInterval;
                Probe();
            }

            m_CurrentSpeed = 0f;
            m_StateTimer -= dt;

            if (m_RestoreCullingAt > 0f && Time.time >= m_RestoreCullingAt && m_State != State.Act)
            {
                m_RestoreCullingAt = -1f;
                if (m_Animator != null) m_Animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            }

            if (m_State != State.Act && Time.time >= m_NextHabitAt)
            {
                StartHabit(); // stops a walk where it is
            }

            if (m_State == State.Act && m_Facing)
            {
                // Turn on the spot towards this machine's player (following them if they move), then stomp.
                if (TryYawToLocalPlayer(out float yaw)) m_TargetYaw = yaw;
                float delta = Mathf.DeltaAngle(m_Yaw, m_TargetYaw);
                float rate = Mathf.Max(m_TurnRate * 2f, FaceTurnRate);
                m_Yaw += Mathf.Clamp(delta, -rate * dt, rate * dt);
                m_FaceTimeLeft -= dt;
                if (Mathf.Abs(delta) < 4f || m_FaceTimeLeft <= 0f) m_Facing = false;
            }
            else if (m_State == State.Act)
            {
                m_ActTime += dt;
                if (m_Habit == Habit.Stomp && !m_Roared && m_ActTime >= RoarDelay)
                {
                    m_Roared = true;
                    if (IsLocalPlayerWithin(RoarRange)) MakeSound(Dinosaurs.Sound);
                }
                if (m_ActTime >= m_ActLength) EndHabit();
            }
            else if (m_State == State.Idle)
            {
                if (m_StateTimer <= 0f)
                {
                    if (m_HomeIsValid && m_WalkClip != null) TryStartWalk();
                    else StartIdle(5f);
                }
            }
            else if (m_Blocked)
            {
                StartIdle(Range(0.5f, 1.5f));
            }
            else
            {
                float delta = Mathf.DeltaAngle(m_Yaw, m_TargetYaw);
                m_Yaw += Mathf.Clamp(delta, -m_TurnRate * dt, m_TurnRate * dt);

                // Slow down for sharp turns, so it turns round rather than marching off sideways.
                m_CurrentSpeed = m_Speed * Mathf.Lerp(1f, 0.3f, Mathf.Clamp01(Mathf.Abs(delta) / 120f));
                Vector3 step = Direction(m_Yaw) * (m_CurrentSpeed * dt);
                transform.position += step;
                m_WalkLeft -= step.magnitude;
                if (m_WalkLeft <= 0f) StartIdle(Range(2f, 7f));
            }

            // Follow the ground smoothly (it's re-measured by Probe).
            Vector3 position = transform.position;
            position.y = Mathf.MoveTowards(position.y, m_GroundY + m_HeightOffset, Mathf.Max(2f, m_Speed) * dt);
            transform.SetPositionAndRotation(position, Quaternion.LookRotation(Direction(m_Yaw)) * m_Tilt);

            UpdateAnimation(dt);
        }

        /// <summary>Every <see cref="ProbeInterval"/>: re-measure the ground, run the safety net, and check the next step.</summary>
        private void Probe()
        {
            Vector3 position = transform.position;

            // Safety net.
            if (Physics.Raycast(position + Vector3.up * (m_Height * 0.5f), Vector3.down, out _, m_Height * 0.5f + 50f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                m_NoGroundTime = 0f;
            }
            else
            {
                m_NoGroundTime += ProbeInterval;
            }

            bool lost = m_NoGroundTime >= LostGroundSeconds ||
                        position.y < m_HomePosition.y - FallLimit ||
                        (m_HomeIsValid && Dinosaurs.IsForbidden(position)) ||
                        (m_HomeIsValid && WaterVolume.IsInWater(new Vector3(position.x, m_GroundY + 0.2f, position.z)));
            if (lost)
            {
                Respawn();
                return;
            }

            if (TryGround(position, m_GroundY, out float ground)) m_GroundY = ground;

            if (m_State == State.Walk)
            {
                float lookahead = Mathf.Max(1f, m_Speed * 0.35f);
                float nextYaw = m_Yaw + Mathf.Clamp(Mathf.DeltaAngle(m_Yaw, m_TargetYaw), -m_TurnRate * 0.35f, m_TurnRate * 0.35f);
                Vector3 next = position + Direction(nextYaw) * lookahead;
                m_Blocked = !IsStepClear(position, m_Yaw, m_GroundY, next, nextYaw);
            }
            else
            {
                m_Blocked = false;
            }
        }

        private void TryStartWalk()
        {
            float length = m_Front + m_Back;
            float distance = Mathf.Clamp(Range(1f, 3f) * length, 6f, 80f);
            Vector3 position = transform.position;
            Vector3 toHome = Flat(m_HomePosition - position);
            bool farFromHome = toHome.magnitude > m_WanderRadius;

            // Try 16 directions all round, roughly the way it's already facing first (or towards home when
            // it's strayed too far), so it meanders rather than spinning on the spot. The big ones in tight
            // spots often have only one or two ways out.
            float preferred = farFromHome ? YawOf(toHome) : m_Yaw;
            float jitter = farFromHome ? 45f : 90f;
            float offset = Range(0f, 22.5f);
            for (int i = 0; i < s_Candidates.Length; i++)
            {
                float yaw = offset + i * 22.5f;
                s_Candidates[i] = new Vector2(yaw, Mathf.Abs(Mathf.DeltaAngle(yaw, preferred)) + Range(0f, jitter));
            }
            System.Array.Sort(s_Candidates, (a, b) => a.y.CompareTo(b.y));

            float check = Mathf.Min(distance, Mathf.Max(length * 0.5f, 6f));
            foreach (Vector2 candidate in s_Candidates)
            {
                float yaw = candidate.x;
                if (!IsPathClear(position, yaw, check)) continue;

                m_TargetYaw = yaw;
                m_WalkLeft = distance;
                m_State = State.Walk;
                m_Blocked = false;
                return;
            }

            StartIdle(Range(1.5f, 3f)); // boxed in for now - try again shortly
        }

        private void StartIdle(float seconds)
        {
            m_State = State.Idle;
            m_StateTimer = seconds;
            m_Blocked = false;
        }

        private void Respawn()
        {
            if (m_State == State.Act) EndHabit();
            transform.position = m_HomePosition;
            m_Yaw = m_TargetYaw = m_HomeYaw;
            m_GroundY = m_HomePosition.y - m_HeightOffset;
            m_NoGroundTime = 0f;
            transform.rotation = Quaternion.LookRotation(Direction(m_Yaw)) * m_Tilt;
            StartIdle(Range(2f, 4f));
        }

        // =====================================================================================
        // Habits
        // =====================================================================================

        private void StartHabit()
        {
            m_NextHabitAt = Time.time + Range(HabitMinSeconds, HabitMaxSeconds);
            m_ActTime = 0f;
            m_Roared = false;
            m_Facing = false;

            if (m_Habit == Habit.Graze)
            {
                if (m_BendBones == null) return;
                m_ActLength = GrazeSeconds;
                m_Tugs = Range(0f, 1f) < 0.5f ? 2 : 3;
                SolveGrazePose();
                m_BendApplied = false;
                // Bones are bent on top of the animation every frame, so they must be animated every frame too.
                if (m_Animator != null) m_Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            else
            {
                if (m_ActClip == null || m_ActClip.length <= 0.01f) return;
                int repeats = m_Habit == Habit.Lunge && Range(0f, 1f) < 0.4f ? 2 : 1;
                m_ActLength = m_ActClip.length * repeats;

                // The T-rex faces whoever it's about to roar at.
                if (m_Habit == Habit.Stomp && IsLocalPlayerWithin(RoarRange) && TryYawToLocalPlayer(out float yaw))
                {
                    m_Facing = true;
                    m_FaceTimeLeft = FaceTimeout;
                    m_TargetYaw = yaw;
                }
            }

            m_State = State.Act;
            m_Blocked = false;
        }

        private void EndHabit()
        {
            m_Facing = false;
            if (m_Habit == Habit.Graze)
            {
                // Straighten up (the animation re-poses the bones it drives anyway; this covers any it doesn't).
                if (m_BendApplied)
                {
                    for (int i = 0; i < m_BendBones.Length; i++) if (m_BendBones[i] != null) m_BendBones[i].localRotation = m_BendBase[i];
                    m_BendApplied = false;
                }
                m_RestoreCullingAt = Time.time + 0.5f;
            }
            StartIdle(Range(1.5f, 4f));
        }

        /// <summary>The flat direction (yaw) from the dinosaur to this machine's player, if there is one.</summary>
        private bool TryYawToLocalPlayer(out float yaw)
        {
            yaw = m_Yaw;
            CorePlayerManager local = NetworkPlayers.Local;
            if (local == null) return false;
            Vector3 to = Flat(local.transform.position - transform.position);
            if (to.sqrMagnitude < 0.01f) return false;
            yaw = YawOf(to);
            return true;
        }

        private bool IsLocalPlayerWithin(float range)
        {
            CorePlayerManager local = NetworkPlayers.Local;
            return local != null && DistanceTo(local.transform.position + Vector3.up * 0.9f) <= range;
        }

        /// <summary>
        /// How far to bend so the snout gets down to the grass: two-legged ones lean the upper body forward
        /// first, then the neck and head bend further in 5° steps until the snout is just above the ground -
        /// or the head points <see cref="MaxHeadDown"/> down, so tall ones (Triceratops) look down at the
        /// grass rather than curling their heads under. Checked offline on all four plant eaters' rigs.
        /// The bones are put back afterwards; LateUpdate does the real bending.
        /// </summary>
        private void SolveGrazePose()
        {
            m_GrazePitch = m_GrazeTorso = 0f;
            if (m_BendBones == null || m_Snout == null) return;

            var saved = new Quaternion[m_BendBones.Length];
            for (int i = 0; i < m_BendBones.Length; i++) if (m_BendBones[i] != null) saved[i] = m_BendBones[i].localRotation;

            float target = m_GroundY + Mathf.Max(0.3f, m_Height * 0.03f);
            float SnoutHeightAt(float pitch, float torso)
            {
                for (int i = 0; i < m_BendBones.Length; i++) if (m_BendBones[i] != null) m_BendBones[i].localRotation = saved[i];
                m_GrazePitch = pitch;
                m_GrazeTorso = torso;
                ApplyBend(1f, 0f, 0f);
                return SnoutPosition().y;
            }

            float lean = m_Upright ? UprightLean : 0f;
            float bestPitch = 0f, bestHeight = float.MaxValue;
            for (float pitch = 5f; pitch <= MaxGrazePitch + 0.01f; pitch += 5f)
            {
                float h = SnoutHeightAt(pitch, lean);
                if (HeadDownAngle() > MaxHeadDown) break;
                if (h < bestHeight) { bestHeight = h; bestPitch = pitch; }
                if (h <= target) break;
            }
            float bestTorso = lean;

            for (int i = 0; i < m_BendBones.Length; i++) if (m_BendBones[i] != null) m_BendBones[i].localRotation = saved[i];
            m_GrazePitch = bestPitch;
            m_GrazeTorso = bestTorso;
        }

        /// <summary>How far below level the head points (head bone to snout), in degrees.</summary>
        private float HeadDownAngle()
        {
            Transform head = m_BendBones[3] != null ? m_BendBones[3] : m_BendBones[2];
            if (head == null) return 0f;
            Vector3 along = SnoutPosition() - head.position;
            return along.sqrMagnitude < 0.000001f ? 0f : Mathf.Asin(Mathf.Clamp(-along.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        private Vector3 SnoutPosition() =>
            m_SnoutLocal == Vector3.zero ? m_Snout.position : m_Snout.TransformPoint(m_SnoutLocal);

        /// <summary>
        /// Bends the bones (on top of whatever pose they're in): <paramref name="amount"/> 0-1 of the graze pose,
        /// plus a side-to-side <paramref name="shake"/> and an up/down <paramref name="nod"/> of the head, in degrees.
        /// Rotations are about the body's own right axis (positive = down), parent first.
        /// </summary>
        private void ApplyBend(float amount, float shake, float nod)
        {
            Vector3 heading = Flat(transform.rotation * m_ModelForward);
            if (heading.sqrMagnitude < 0.0001f) return;
            Vector3 right = Vector3.Cross(Vector3.up, heading.normalized);

            Bend(m_BendBones[0], m_GrazeTorso * amount);
            Bend(m_BendBones[1], m_GrazePitch * ShouldersShare * amount);
            Bend(m_BendBones[2], m_GrazePitch * NeckShare * amount);
            Transform head = m_BendBones[3];
            if (head != null)
            {
                head.rotation = Quaternion.AngleAxis(shake, Vector3.up) * Quaternion.AngleAxis(m_GrazePitch * HeadShare * amount + nod, right) * head.rotation;
            }

            void Bend(Transform bone, float degrees)
            {
                if (bone != null && degrees != 0f) bone.rotation = Quaternion.AngleAxis(degrees, right) * bone.rotation;
            }
        }

        /// <summary>
        /// The graze, <paramref name="t"/> seconds in: head down (eased), then a few tugs at the grass (a quick
        /// shake while pulling up a little), chewing in between, then head back up.
        /// </summary>
        private void GrazeMotion(float t, out float amount, out float shake, out float nod)
        {
            float hold = GrazeSeconds - 2f * GrazeLower;
            if (t < GrazeLower) amount = Mathf.SmoothStep(0f, 1f, t / GrazeLower);
            else if (t > GrazeSeconds - GrazeLower) amount = Mathf.SmoothStep(0f, 1f, (GrazeSeconds - t) / GrazeLower);
            else amount = 1f;

            shake = 0f;
            float tug = 0f;
            float since = t - GrazeLower - 0.3f;
            float spacing = (hold - 0.3f) / Mathf.Max(1, m_Tugs);
            if (since >= 0f && since < hold - 0.3f)
            {
                float u = (since % spacing) / 0.8f;            // each tug lasts 0.8 s
                if (u < 1f)
                {
                    float envelope = Mathf.Sin(u * Mathf.PI);
                    shake = ShakeDegrees * envelope * Mathf.Sin(u * Mathf.PI * 2f * 2.5f);
                    tug = envelope;
                }
            }
            float chew = (1f - tug) * Mathf.Sin(t * Mathf.PI * 2f * 1.6f) * 2.5f;
            nod = (-6f * tug + chew) * amount;                 // pull up a little while tugging
            shake *= amount;
        }

        /// <summary>Grazing is drawn here, after the animation has posed the bones for this frame.</summary>
        private void LateUpdate()
        {
            if (m_State != State.Act || m_Habit != Habit.Graze || m_BendBones == null) return;

            for (int i = 0; i < m_BendBones.Length; i++)
            {
                Transform bone = m_BendBones[i];
                if (bone == null) continue;
                // If the animation didn't write this bone this frame, take our bend back off before re-applying it.
                if (m_BendApplied && bone.localRotation == m_BendWritten[i]) bone.localRotation = m_BendBase[i];
                m_BendBase[i] = bone.localRotation;
            }

            GrazeMotion(m_ActTime, out float amount, out float shake, out float nod);
            ApplyBend(amount, shake, nod);

            for (int i = 0; i < m_BendBones.Length; i++)
            {
                if (m_BendBones[i] != null) m_BendWritten[i] = m_BendBones[i].localRotation;
            }
            m_BendApplied = true;
        }

        // =====================================================================================
        // Where it can go
        // =====================================================================================

        // Points under the body, as (fraction of front/back extent, fraction of half width). The middle one is
        // where the legs are: it needs ground at a walkable height. The others only mustn't hang over a drop
        // (an edge, a pit) - something tall under the head or tail is fine - and the back ones only if they
        // have ground now (a tail already over an edge is fine).
        private static readonly Vector2[] FrontPoints = { new Vector2(0.75f, 0f), new Vector2(0.6f, -0.8f), new Vector2(0.6f, 0.8f), new Vector2(0f, 0f) };
        private static readonly Vector2[] BackPoints = { new Vector2(-0.5f, -0.8f), new Vector2(-0.5f, 0.8f) };

        /// <summary>Checks a straight walk of <paramref name="distance"/> towards <paramref name="yaw"/>, in body-length hops.</summary>
        private bool IsPathClear(Vector3 from, float yaw, float distance)
        {
            float hop = Mathf.Max(2f, (m_Front + m_Back) * 0.25f);
            Vector3 direction = Direction(yaw);
            Vector3 current = from;
            float currentYaw = m_Yaw;
            float ground = m_GroundY;

            for (float travelled = 0f; travelled < distance;)
            {
                float step = Mathf.Min(hop, distance - travelled);
                Vector3 next = current + direction * step;
                if (!IsStepClear(current, currentYaw, ground, next, yaw)) return false;
                if (TryGround(next, ground, out float g)) ground = g;
                current = next;
                currentYaw = yaw;
                travelled += step;
            }
            return true;
        }

        /// <summary>Can the body move from one pose to the next without leaving the ground, stepping up or down, or entering water or a hole?</summary>
        private bool IsStepClear(Vector3 fromPos, float fromYaw, float fromGround, Vector3 toPos, float toYaw)
        {
            if (Dinosaurs.IsForbidden(toPos)) return false;

            for (int i = 0; i < FrontPoints.Length + BackPoints.Length; i++)
            {
                bool front = i < FrontPoints.Length;
                Vector2 p = front ? FrontPoints[i] : BackPoints[i - FrontPoints.Length];
                bool legs = p == Vector2.zero;
                Vector3 now = BodyPoint(fromPos, fromYaw, p);
                Vector3 next = BodyPoint(toPos, toYaw, p);

                bool hadGround = TryGround(now, fromGround, out float groundNow);
                if (!hadGround)
                {
                    if (!front) continue;
                    groundNow = fromGround;
                }

                if (!TryGround(next, groundNow, out float groundNext))
                {
                    // Nothing within reach: a drop (edge, pit) - or something tall standing there.
                    if (legs || !IsTallThingAt(next, groundNow)) return false;
                    continue;
                }
                if (groundNext < groundNow - MaxStep) return false;                    // a drop
                if (legs && groundNext > groundNow + MaxStep) return false;            // a step up
                if (front && Dinosaurs.IsForbidden(next)) return false;
                if (WaterVolume.IsInWater(new Vector3(next.x, groundNext + 0.2f, next.z))) return false;
            }

            // No wall or fence in the way of the legs. Bigger dinosaurs step over more (benches, low walls).
            // Thin things (posts, tree trunks) are ignored - it just walks past them.
            Vector3 direction = toPos - fromPos;
            float travel = direction.magnitude;
            Vector3 heading = travel > 0.001f ? direction / travel : Direction(toYaw);
            Vector3 right = Vector3.Cross(Vector3.up, heading);
            float reach = m_Front * 0.6f + travel;
            float legHeight = fromGround + Mathf.Max(MaxStep + 0.15f, Mathf.Min(m_Height * 0.12f, 2.5f));
            for (int side = -1; side <= 1; side++)
            {
                Vector3 start = fromPos + right * (side * m_HalfWidth * 0.7f);
                if (IsWallAhead(new Vector3(start.x, legHeight, start.z), heading, right, reach)) return false;
            }
            return true;
        }

        /// <summary>Something wide (a wall, a fence) within <paramref name="distance"/>: the ray hits, and so does one beside it, 1.5 m to either side.</summary>
        private static bool IsWallAhead(Vector3 origin, Vector3 direction, Vector3 right, float distance)
        {
            float hit = HitDistance(origin, direction, distance);
            if (hit > distance) return false;
            return HitDistance(origin + right * 1.5f, direction, hit + 2f) <= hit + 2f ||
                   HitDistance(origin - right * 1.5f, direction, hit + 2f) <= hit + 2f;
        }

        /// <summary>True if something stands at <paramref name="point"/> taller than the ground probe reaches (rather than there being a drop).</summary>
        private static bool IsTallThingAt(Vector3 point, float near)
        {
            const float from = 40f;
            var origin = new Vector3(point.x, near + from, point.z);
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, s_Hits, from + MaxStep, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                if (!IsPlayer(s_Hits[i].collider) && s_Hits[i].point.y >= near - MaxStep) return true;
            }
            return false;
        }

        private Vector3 BodyPoint(Vector3 origin, float yaw, Vector2 p)
        {
            Vector3 forward = Direction(yaw);
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            float along = p.x >= 0f ? p.x * m_Front : p.x * m_Back;
            return origin + forward * along + right * (p.y * m_HalfWidth);
        }

        /// <summary>The ground height at <paramref name="point"/>'s x/z, if there's ground within <see cref="ProbeUp"/> above or below <paramref name="near"/>.</summary>
        private static bool TryGround(Vector3 point, float near, out float height)
        {
            var origin = new Vector3(point.x, near + ProbeUp, point.z);
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, s_Hits, ProbeUp * 2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            height = 0f;
            for (int i = 0; i < count; i++)
            {
                if (IsPlayer(s_Hits[i].collider) || s_Hits[i].distance >= best) continue;
                best = s_Hits[i].distance;
                height = s_Hits[i].point.y;
            }
            return best < float.MaxValue;
        }

        /// <summary>Distance to the nearest non-player hit along the ray, or float.MaxValue.</summary>
        private static float HitDistance(Vector3 origin, Vector3 direction, float distance)
        {
            int count = Physics.RaycastNonAlloc(origin, direction, s_Hits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (!IsPlayer(s_Hits[i].collider)) best = Mathf.Min(best, s_Hits[i].distance);
            }
            return best;
        }

        private static bool IsPlayer(Collider collider) =>
            collider is CharacterController || collider.GetComponentInParent<CorePlayerManager>() != null;

        // =====================================================================================
        // Animation & sound
        // =====================================================================================

        private void UpdateAnimation(float dt)
        {
            if (!m_Graph.IsValid()) return;

            // Turning on the spot to face the player uses the walk (slowed) so the feet shuffle round.
            bool walking = (m_State == State.Walk && m_CurrentSpeed > 0.01f) || (m_State == State.Act && m_Facing);
            float target = walking && m_WalkClip != null ? 1f : 0f;
            if (m_IdleClip == null) target = 1f;
            m_WalkWeight = Mathf.MoveTowards(m_WalkWeight, target, dt / 0.35f);

            m_IdleTime += dt;
            float rate = m_Speed > 0f ? Mathf.Clamp(m_CurrentSpeed / m_Speed, 0.5f, 1.2f) : 1f;
            m_WalkTime += dt * (walking ? rate : 1f);

            // The T-rex's jump / the raptor's attack, faded in and out over the idle.
            float act = 0f;
            if (m_State == State.Act && m_ActClip != null && m_Habit != Habit.Graze && m_ActClip.length > 0.01f)
            {
                act = Mathf.Clamp01(Mathf.Min(m_ActTime / 0.15f, (m_ActLength - m_ActTime) / 0.25f));
                m_ActPlayable.SetTime(Mathf.Min(m_ActTime, m_ActLength - 0.001f) % m_ActClip.length);
            }

            if (m_IdleClip != null) m_IdlePlayable.SetTime(m_IdleTime % m_IdleClip.length);
            if (m_WalkClip != null) m_WalkPlayable.SetTime(m_WalkTime % m_WalkClip.length);
            m_Mixer.SetInputWeight(0, m_IdleClip != null ? (1f - m_WalkWeight) * (1f - act) : 0f);
            m_Mixer.SetInputWeight(1, m_WalkClip != null ? m_WalkWeight * (1f - act) : 0f);
            m_Mixer.SetInputWeight(2, m_ActClip != null ? act : 0f);
        }

        /// <summary>Plays <paramref name="clip"/> from the dinosaur, at the Sound Effects volume (unless it already is).</summary>
        public void MakeSound(AudioClip clip)
        {
            if (clip == null || m_Voice == null || m_Voice.isPlaying) return;
            m_Voice.clip = clip;
            m_Voice.volume = AudioVolumeService.GetMultiplier(AudioCategory.SoundEffects);
            m_Voice.Play();
        }

        /// <summary>Distance from <paramref name="point"/> to the dinosaur's bounding box (0 inside it).</summary>
        public float DistanceTo(Vector3 point)
        {
            float best = float.MaxValue;
            foreach (Renderer r in m_Renderers)
            {
                if (r == null) continue;
                best = Mathf.Min(best, Vector3.Distance(point, r.bounds.ClosestPoint(point)));
            }
            return best == float.MaxValue ? Vector3.Distance(point, transform.position) : best;
        }

        // =====================================================================================
        // Helpers
        // =====================================================================================

        private float Range(float min, float max) => min + (float)m_Random.NextDouble() * (max - min);

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        private static float YawOf(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

        private static Vector3 Direction(float yaw)
        {
            float r = yaw * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r));
        }
    }
}
