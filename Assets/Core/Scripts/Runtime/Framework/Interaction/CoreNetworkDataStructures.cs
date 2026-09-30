using System;
using UnityEngine;
using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// A struct that encapsulates all the necessary information about a single "hit" event.
    /// This is used to pass data from a source of damage (like a projectile or weapon) to a target
    /// that implements the <see cref="IHittable"/> interface. It is a Fusion INetworkStruct so it can be
    /// sent in an RPC from the hitting client to the target's State Authority for processing.
    /// </summary>
    [Serializable]
    public struct HitInfo : INetworkStruct
    {
        /// <summary>
        /// The base amount of damage or effect magnitude.
        /// </summary>
        public float amount;

        /// <summary>
        /// The world-space coordinate where the hit occurred.
        /// </summary>
        public Vector3 hitPoint;

        /// <summary>
        /// The surface normal at the point of impact.
        /// </summary>
        public Vector3 hitNormal;

        /// <summary>
        /// The player id (see NetworkPlayers) of the player that initiated the hit.
        /// </summary>
        public ulong attackerId;

        /// <summary>
        /// The physical force to be applied at the hit point, used for knockback or physics reactions.
        /// </summary>
        public Vector3 impactForce;
    }

    /// <summary>
    /// A struct representing the current state of a single gameplay stat (e.g., Health, Stamina) at runtime.
    /// Stored in a networked array on <see cref="CoreStatsHandler"/>, so it's a Fusion INetworkStruct
    /// (plain blittable fields only). Implements <see cref="IEquatable{T}"/> for change detection.
    /// </summary>
    public struct RuntimeStat : INetworkStruct, IEquatable<RuntimeStat>
    {
        /// <summary>
        /// A unique integer hash representing the stat's name (e.g., from Animator.StringToHash("Health")).
        /// Using a hash is more network-efficient than sending the full string name.
        /// </summary>
        public int StatHash;

        /// <summary>
        /// The current floating-point value of the stat.
        /// </summary>
        public float CurrentValue;

        /// <summary>
        /// The unique identifier of the player who caused this stat modification.
        /// Used to track the source of damage, healing, or other stat changes for attribution purposes.
        /// </summary>
        public ulong SourcePlayerId;

        /// <summary>
        /// The type of source that caused this stat modification (e.g., player, environment, ability).
        /// </summary>
        public ModificationSource SourceType;

        /// <summary>
        /// Compares this RuntimeStat instance to another for equality.
        /// </summary>
        public bool Equals(RuntimeStat other)
        {
            return StatHash == other.StatHash &&
                   Mathf.Approximately(CurrentValue, other.CurrentValue) &&
                   SourcePlayerId == other.SourcePlayerId &&
                   SourceType == other.SourceType;
        }

        public override bool Equals(object obj) => obj is RuntimeStat other && Equals(other);

        public override int GetHashCode() => StatHash;
    }
}
