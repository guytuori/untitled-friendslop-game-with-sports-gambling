namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Defines the contract for any game object that can be "hit" by another entity, such as a projectile or a melee attack.
    /// This interface provides a standardized system for receiving hit information. Components implementing this interface, like
    /// <see cref="HitProcessor"/>, can be attached to players, enemies, or destructible objects to handle incoming damage or forces.
    /// It includes a client-side entry point (<see cref="OnHit"/>) and the RPC that carries the hit to the object's
    /// State Authority (<see cref="SubmitHitRpc"/>).
    /// </summary>
    public interface IHittable
    {
        #region Public Methods

        /// <summary>
        /// The primary method called on a client when a hit is detected on this object.
        /// The implementation of this method is responsible for getting the hit data to the object's
        /// State Authority for processing.
        /// </summary>
        /// <param name="info">A struct containing all relevant data about the hit (damage, position, attacker, etc.).</param>
        void OnHit(HitInfo info);

        /// <summary>
        /// Sends the hit information to the object's State Authority (a Fusion RPC in <see cref="HitProcessor"/>).
        /// </summary>
        /// <param name="info">The hit data to be sent to the authority.</param>
        void SubmitHitRpc(HitInfo info);

        #endregion
    }
}
