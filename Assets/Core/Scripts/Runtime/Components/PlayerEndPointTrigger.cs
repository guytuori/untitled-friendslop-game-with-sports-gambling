using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>The invisible trigger box a <see cref="PlayerEndPoint"/> builds for itself (added at runtime).</summary>
    [RequireComponent(typeof(Collider))]
    public class PlayerEndPointTrigger : MonoBehaviour
    {
        private const float RetrySeconds = 0.5f;

        private PlayerEndPoint m_EndPoint;
        private float m_LastReportTime = float.NegativeInfinity;

        private void Awake()
        {
            m_EndPoint = GetComponentInParent<PlayerEndPoint>();
        }

        private void OnTriggerEnter(Collider other) => Check(other);

        // Also while standing in it, in case the round only just started or a report was lost.
        private void OnTriggerStay(Collider other) => Check(other);

        private void Check(Collider other)
        {
            if (m_EndPoint == null) return;
            if (!other.TryGetComponent(out CorePlayerManager player)) return;
            if (!player.IsOwner || !player.IsSpawned) return;

            RoundTimer round = RoundTimer.Instance;
            if (round == null || !round.IsPlaying || round.LocalPlayerFinished) return;
            if (Time.time - m_LastReportTime < RetrySeconds) return;

            m_LastReportTime = Time.time;
            m_EndPoint.ReportLocalPlayerEntered();
        }
    }
}
