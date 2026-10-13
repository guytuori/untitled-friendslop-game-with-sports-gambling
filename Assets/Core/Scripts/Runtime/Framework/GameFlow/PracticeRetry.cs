using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Practice Challenges' quick Retry: starts the run over on the spot, without reloading anything.
    /// The player goes back to the spawn point (facing its way, camera level), with full health and
    /// stamina and a score of 0. Every pickup comes back, the challenge can be claimed again, the respawn
    /// history is forgotten, and the stopwatch restarts.
    ///
    /// This works because a practice session has exactly one player, who is also the master client, so
    /// the networked resets (ChallengeManager, PickupManager) are this machine's to make.
    /// </summary>
    public static class PracticeRetry
    {
        /// <summary>
        /// Change Challenge without reloading: swaps the challenge (and the practice map, if it's the other
        /// size) in place, then starts the run like <see cref="Run"/>. False (nothing changed) if it couldn't.
        /// </summary>
        public static bool ChangeChallenge(string challengeId)
        {
            if (!PracticeMode.IsChallenge || string.IsNullOrEmpty(challengeId) || !FusionSessionService.HasInstance) return false;
            MatchLayout layout = MatchLayout.Current;
            if (layout == null || NetworkPlayers.Local == null) return false;

            FusionSessionService session = FusionSessionService.Instance;
            string previous = session.PracticeChallengeId;
            session.SetPracticeChallenge(challengeId);
            if (!layout.RebuildForPractice())
            {
                session.SetPracticeChallenge(previous);
                return false;
            }

            if (PickupManager.Instance != null) PickupManager.Instance.RescanForPractice();
            return Run();
        }

        /// <summary>False (nothing changed) if there's no local player or spawn point to go back to.</summary>
        public static bool Run()
        {
            if (!PracticeMode.IsActive) return false;

            CorePlayerManager player = NetworkPlayers.Local;
            if (player == null || !player.IsSpawned) return false;

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || !gameManager.TryGetSpawnPoint(out Vector3 position, out Quaternion rotation)) return false;

            // The course: challenges claimable again, every pickup back.
            if (ChallengeManager.Instance != null) ChallengeManager.Instance.ResetForPracticeRetry();
            if (PickupManager.Instance != null) PickupManager.Instance.ResetForPracticeRetry();

            // The player.
            var score = player.GetComponent<PlayerScore>();
            if (score != null) score.SetScore(0);

            CoreMovement movement = player.CoreMovement;
            if (movement != null)
            {
                movement.transform.rotation = rotation;
                movement.SetPosition(position);
                movement.ResetMovementForces();
            }
            if (player.CoreCamera != null) player.CoreCamera.ResetLook(rotation.eulerAngles.y);

            var stats = player.GetComponent<CoreStatsHandler>();
            if (stats != null)
            {
                ulong id = player.OwnerClientId;
                stats.ModifyStat(StatKeys.Health, 1000f, id, ModificationSource.Regeneration);
                stats.ModifyStat(StatKeys.Stamina, 1000f, id, ModificationSource.Regeneration);
            }
            if (player.PlayerState != null && player.PlayerState.LifeState == PlayerLifeState.Eliminated)
            {
                player.PlayerState.SetLifeState(PlayerLifeState.Respawned);
            }

            if (LocalRespawnTracker.Local != null) LocalRespawnTracker.Local.ResetHistory();

            PracticeMode.StartRun();
            return true;
        }
    }
}
