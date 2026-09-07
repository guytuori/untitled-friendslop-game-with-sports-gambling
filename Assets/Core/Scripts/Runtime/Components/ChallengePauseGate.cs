using Unity.Netcode;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Freezes (and unfreezes) this player's own movement input whenever ChallengeManager's global pause
    /// flag changes - i.e. during a challenge's betting window. Implemented as an IPlayerAddon (the same
    /// extension point WallClimbAbility, JumpAbility etc. use) purely so it can sit on the player prefab as
    /// an independent, optional add-on: CorePlayerManager and CoreMovement need no changes at all to
    /// support pausing, and removing this component removes pausing with nothing else to touch.
    /// </summary>
    public class ChallengePauseGate : NetworkBehaviour, IPlayerAddon
    {
        private CorePlayerManager m_PlayerManager;

        public void Initialize(CorePlayerManager playerManager)
        {
            m_PlayerManager = playerManager;
        }

        public void OnPlayerSpawn()
        {
            if (!IsOwner) return;
            if (ChallengeManager.Instance == null) return;

            ChallengeManager.Instance.IsPaused.OnValueChanged += HandlePausedChanged;
            HandlePausedChanged(false, ChallengeManager.Instance.IsPaused.Value);
        }

        public void OnPlayerDespawn()
        {
            if (!IsOwner) return;
            if (ChallengeManager.Instance == null) return;

            ChallengeManager.Instance.IsPaused.OnValueChanged -= HandlePausedChanged;
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
