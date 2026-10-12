using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Shows each player avatar as the character that player picked on the Change Profile screen. Every
    /// client does this for every avatar, from the character id in SessionProfiles - nothing about the
    /// model itself is networked. Added automatically to every avatar when it spawns (NetworkPlayers).
    ///
    /// The swap keeps the existing model object (the one with the Animator and CoreAnimator, which the
    /// rest of the player - Fusion's NetworkMecanimAnimator, NetworkTransform, VisualsAddon - already points
    /// at) and only replaces what's under it: the new model's skeleton and meshes move in, the Animator
    /// takes the new model's Humanoid avatar and rebinds. The shared Humanoid animations then just play on
    /// the new skeleton.
    ///
    /// What's shown also follows THIS player's spider settings (CharacterCatalog.ResolveForViewer):
    /// Arachnophobia shows other players' spiders as Girl, Arachnophilia shows everyone as the spider.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerCharacterModel : MonoBehaviour
    {
        private CorePlayerManager m_Player;
        private string m_AppliedId = string.Empty;

        /// <summary>Adds this to a freshly spawned avatar (once).</summary>
        public static void Attach(CorePlayerManager player)
        {
            if (player == null) return;
            var model = player.GetComponent<PlayerCharacterModel>();
            if (model == null) model = player.gameObject.AddComponent<PlayerCharacterModel>();
            model.m_Player = player;
            model.Refresh();
        }

        private void OnEnable()
        {
            SessionProfiles.Changed += Refresh;
            GameplaySettings.Changed += Refresh;
        }

        private void OnDisable()
        {
            SessionProfiles.Changed -= Refresh;
            GameplaySettings.Changed -= Refresh;
        }

        private void Refresh()
        {
            if (m_Player == null || !m_Player.IsSpawned) return;

            int playerId = (int)m_Player.OwnerClientId;
            string pickedId = SessionProfiles.TryGet(playerId, out SessionProfile profile) ? profile.CharacterId : string.Empty;

            CharacterCatalog catalog = CharacterCatalog.Load();
            if (catalog == null) return;

            // What THIS player sees for that pick, after their spider settings.
            GameplaySettingsData settings = GameplaySettings.Current;
            CharacterCatalog.Entry entry = catalog.ResolveForViewer(pickedId, settings.ArachnophobiaMode, settings.ArachnophiliaMode);
            if (entry == null)
            {
                if (!string.IsNullOrEmpty(pickedId) && catalog.Find(pickedId) == null && pickedId != m_AppliedId)
                {
                    Debug.LogWarning($"[PlayerCharacterModel] Unknown character '{pickedId}' for player {playerId} - keeping the default model.");
                    m_AppliedId = pickedId;
                }
                return; // the default model
            }

            if (entry.id == m_AppliedId) return;
            if (entry.model == null) return;

            if (Apply(entry.model))
            {
                m_AppliedId = entry.id;
            }
        }

        private bool Apply(GameObject characterModel)
        {
            var coreAnimator = GetComponentInChildren<CoreAnimator>(true);
            Animator animator = coreAnimator != null ? coreAnimator.Animator : null;
            if (animator == null) animator = coreAnimator != null ? coreAnimator.GetComponent<Animator>() : null;
            if (animator == null)
            {
                Debug.LogWarning("[PlayerCharacterModel] The player has no model with a CoreAnimator/Animator - can't swap characters.", this);
                return false;
            }

            Transform modelRoot = animator.transform;

            GameObject instance = Instantiate(characterModel, modelRoot.parent);

            Animator sourceAnimator = instance.GetComponent<Animator>();
            Avatar avatar = sourceAnimator != null ? sourceAnimator.avatar : null;
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                Debug.LogWarning($"[PlayerCharacterModel] '{characterModel.name}' has no valid Humanoid avatar - keeping the current model.", this);
                Destroy(instance);
                return false;
            }

            // Detach (not just Destroy, which waits for the end of the frame) so Rebind only sees the new bones.
            for (int i = modelRoot.childCount - 1; i >= 0; i--)
            {
                Transform child = modelRoot.GetChild(i);
                child.SetParent(null, false);
                Destroy(child.gameObject);
            }

            modelRoot.localRotation = characterModel.transform.localRotation;
            modelRoot.localScale = characterModel.transform.localScale;

            while (instance.transform.childCount > 0)
            {
                Transform child = instance.transform.GetChild(0);
                child.SetParent(modelRoot, false);
            }
            Destroy(instance);

            animator.avatar = avatar;
            animator.Rebind();
            animator.Update(0f);

            Renderer[] renderers = modelRoot.GetComponentsInChildren<Renderer>(true);
            foreach (VisualsAddon visuals in GetComponentsInChildren<VisualsAddon>(true))
            {
                visuals.SetModelRenderers(renderers);
            }

            return true;
        }
    }
}
