namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Marks a MonoBehaviour that must always be present on every player prefab, but that isn't part of
    /// the per-player addon lifecycle (see <see cref="IPlayerAddon"/>) - e.g. <see cref="PlayerScore"/>,
    /// which just needs to exist on the player, not to hook OnPlayerSpawn/OnLifeStateChanged/etc.
    ///
    /// SceneRefreshSetup's player-prefab refresh step (Friendslop > Refresh Everything From Assets)
    /// discovers every concrete type implementing either this interface or IPlayerAddon (via
    /// UnityEditor.TypeCache) and makes sure it's present on every player prefab in the project. A brand
    /// new "every player needs this" component just needs to implement this interface (no methods to
    /// implement - it's a pure marker) to be picked up automatically, with no changes needed to
    /// SceneRefreshSetup itself.
    /// </summary>
    public interface IPlayerRequiredComponent
    {
    }
}
