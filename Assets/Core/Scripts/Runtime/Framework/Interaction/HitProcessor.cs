using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// An abstract base class for components that can process hit information. It implements the <see cref="IHittable"/>
    /// interface to provide a standardized way of receiving hit data. Its primary role is to act as a client-side
    /// entry point for a hit, which it then forwards to the object's State Authority via an RPC for processing.
    /// Derived classes must implement the <see cref="HandleHit"/> method to define the specific consequences of a
    /// hit, such as applying damage or a physical force.
    /// </summary>
    /// <remarks>
    /// Network Flow (Photon Fusion, Shared mode):
    /// 1. A client detects a hit → calls OnHit()
    /// 2. OnHit() checks authority:
    ///    - If this client has State Authority over the hit object: processes immediately via HandleHit()
    ///    - Otherwise: sends an RPC to the State Authority
    /// 3. The State Authority receives the RPC → calls HandleHit()
    /// 4. HandleHit() applies game logic (damage, physics, etc.)
    /// </remarks>
    public abstract class HitProcessor : CoreNetworkBehaviour, IHittable
    {
        #region IHittable Implementation

        /// <summary>
        /// Public entry point called when this object is hit by something on a client.
        /// This method takes the hit information and forwards it to the authority via an RPC for processing.
        /// </summary>
        /// <param name="info">A struct containing all relevant data about the hit (damage, position, attacker, etc.).</param>
        public void OnHit(HitInfo info)
        {
            if (!IsSpawned) return;

            if (HasAuthority)
            {
                // We're already on the authoritative instance, process the hit directly
                HandleHit(info);
            }
            else
            {
                // Send the hit to the object's State Authority
                SubmitHitRpc(info);
            }
        }

        /// <summary>
        /// An RPC sent to the State Authority. This receives the hit information on the authoritative
        /// instance and passes it to the HandleHit method for processing.
        /// </summary>
        /// <param name="info">The hit data sent from the client.</param>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void SubmitHitRpc(HitInfo info)
        {
            HandleHit(info);
        }

        #endregion

        #region Protected Abstract Methods

        /// <summary>
        /// The core logic that defines what happens when this object is hit. This method must be implemented by
        /// any class that inherits from HitProcessor. It is called only on the State Authority's instance.
        /// </summary>
        /// <param name="info">The hit information to be processed.</param>
        protected abstract void HandleHit(HitInfo info);

        #endregion
    }
}
