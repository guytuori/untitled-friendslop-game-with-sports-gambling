namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Marks a MonoBehaviour/NetworkBehaviour that must exist exactly once, placed directly in the gameplay
    /// scene (not spawned from a prefab at runtime) - e.g. <see cref="RoundTimer"/>, <see cref="ChallengeManager"/>.
    ///
    /// SceneRefreshSetup's game-logic refresh step (Friendslop > Refresh Everything From Assets) discovers
    /// every concrete type implementing this interface (via UnityEditor.TypeCache) and makes sure exactly one
    /// instance exists directly in "[BB] Core.unity", creating it if missing. A brand new manager-style class
    /// (the next RoundTimer/ChallengeManager) just needs to implement this interface (no methods required -
    /// it's a pure marker) to be picked up automatically, with no changes needed to SceneRefreshSetup itself.
    /// </summary>
    public interface ISceneSingleton
    {
    }
}
