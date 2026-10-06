using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The end of a map: a player who touches it is finished for the round (see RoundTimer - bonus points
    /// for the time left on the bonus timer, then they fade out and can't wager any more).
    ///
    /// Set-up needs nothing by hand: any object named "player_end_point..." (the player_end_point.glb
    /// tile, placed in a map) gets this component at runtime - see <see cref="SetUpAll"/>, called by
    /// MatchLayout when it builds the map and by RoundTimer for maps placed directly in a scene. It can also
    /// be added through Friendslop > Set Object Property > Player End Point, or by hand.
    ///
    /// On Awake it builds an invisible trigger box over the object's footprint, reaching
    /// <see cref="triggerHeight"/> above it (so jumping onto or over the tile counts), and gives the tile a
    /// solid collider if it has none, so it can be stood on. Only the LOCAL player's own touch is reported
    /// (every machine simulates every player's collider - same rule as ChallengeZoneTrigger).
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerEndPoint : MonoBehaviour
    {
        /// <summary>Objects whose name starts with this (ignoring case) become end points automatically.</summary>
        public const string NamePrefix = "player_end_point";

        [Tooltip("How far above the tile the trigger reaches, in meters.")]
        [SerializeField] private float triggerHeight = 3f;

        [Tooltip("How far below the tile's top the trigger starts, in meters.")]
        [SerializeField] private float triggerDepth = 0.5f;

        private bool m_Built;

        /// <summary>Adds a PlayerEndPoint to every "player_end_point..." object under <paramref name="root"/> that doesn't have one.</summary>
        public static int SetUpAll(Transform root)
        {
            if (root == null) return 0;

            int added = 0;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith(NamePrefix, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (t.GetComponentInParent<PlayerEndPoint>(true) != null) continue; // already part of one
                t.gameObject.AddComponent<PlayerEndPoint>();
                added++;
            }
            return added;
        }

        /// <summary><see cref="SetUpAll"/> for every root object of every loaded scene.</summary>
        public static void SetUpAllInLoadedScenes()
        {
            for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects()) SetUpAll(root.transform);
            }
        }

        private void Awake()
        {
            Build();
        }

        private void Build()
        {
            if (m_Built) return;
            m_Built = true;

            // Solid enough to stand on, if the tile came in without any collision.
            if (GetComponent<Collider>() == null && TryGetComponent(out MeshFilter meshFilter) && meshFilter.sharedMesh != null)
            {
                gameObject.AddComponent<MeshCollider>().sharedMesh = meshFilter.sharedMesh;
            }

            Bounds bounds;
            if (!TryGetRendererBounds(out bounds))
            {
                bounds = new Bounds(transform.position, new Vector3(2f, 0.2f, 2f));
            }

            float bottom = bounds.max.y - triggerDepth;
            float top = bounds.max.y + triggerHeight;
            var center = new Vector3(bounds.center.x, (bottom + top) * 0.5f, bounds.center.z);
            var size = new Vector3(Mathf.Max(bounds.size.x, 0.5f), top - bottom, Mathf.Max(bounds.size.z, 0.5f));

            var triggerObject = new GameObject("PlayerEndPointTrigger");
            triggerObject.transform.SetParent(transform, false);
            triggerObject.transform.SetPositionAndRotation(center, Quaternion.identity);
            triggerObject.layer = gameObject.layer;

            Vector3 lossy = triggerObject.transform.lossyScale;
            var box = triggerObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(size.x / Mathf.Max(0.0001f, Mathf.Abs(lossy.x)),
                                   size.y / Mathf.Max(0.0001f, Mathf.Abs(lossy.y)),
                                   size.z / Mathf.Max(0.0001f, Mathf.Abs(lossy.z)));

            triggerObject.AddComponent<PlayerEndPointTrigger>();
        }

        private bool TryGetRendererBounds(out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        /// <summary>Called by the trigger on the touching player's own machine.</summary>
        internal void ReportLocalPlayerEntered()
        {
            RoundTimer round = RoundTimer.Instance;
            if (round == null)
            {
                Debug.LogWarning("[PlayerEndPoint] Reached the end point, but there's no RoundTimer in this scene.", this);
                return;
            }
            round.ReportLocalPlayerFinished();
        }
    }
}
