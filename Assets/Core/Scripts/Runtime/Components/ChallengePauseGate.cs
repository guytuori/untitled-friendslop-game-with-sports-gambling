using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Freezes (and unfreezes) this player's own movement input whenever ChallengeManager's global pause
    /// flag changes - i.e. during a challenge's betting window. Implemented as an IPlayerAddon (the same
    /// extension point WallClimbAbility, JumpAbility etc. use) purely so it can sit on the player prefab as
    /// an independent, optional add-on: CorePlayerManager and CoreMovement need no changes at all to
    /// support pausing, and removing this component removes pausing with nothing else to touch.
    /// Only does anything on the player's own client (the owner).
    /// </summary>
    public class ChallengePauseGate : MonoBehaviour, IPlayerAddon
    {
        private CorePlayerManager m_PlayerManager;
        private ChallengeManager m_Subscribed;

        public void Initialize(CorePlayerManager playerManager)
        {
            m_PlayerManager = playerManager;
        }

        public void OnPlayerSpawn()
        {
            if (m_PlayerManager == null || !m_PlayerManager.IsOwner) return;
            if (ChallengeManager.Instance == null) return;

            m_Subscribed = ChallengeManager.Instance;
            m_Subscribed.PausedChanged += HandlePausedChanged;
            HandlePausedChanged(false, m_Subscribed.IsPaused);
        }

        public void OnPlayerDespawn()
        {
            if (m_Subscribed == null) return;

            m_Subscribed.PausedChanged -= HandlePausedChanged;
            m_Subscribed = null;
        }

        public void OnLifeStateChanged(PlayerLifeState previousState, PlayerLifeState newState)
        {
            // Pausing doesn't care about life state - nothing to do here.
        }

        private void HandlePausedChanged(bool previousValue, bool isPaused)
        {
            m_PlayerManager?.SetMovementInputEnabled(!isPaused);
        }
    }
}
