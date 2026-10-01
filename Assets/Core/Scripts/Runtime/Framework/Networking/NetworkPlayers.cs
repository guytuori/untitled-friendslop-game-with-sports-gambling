using System;
using System.Collections.Generic;
using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Every spawned player avatar on this client, keyed by player id - the Fusion replacement for NGO's
    /// NetworkManager.ConnectedClientsIds / SpawnManager.GetPlayerNetworkObject / LocalClient.PlayerObject.
    /// CorePlayerManager registers itself in OnNetworkSpawn and unregisters in OnNetworkDespawn, so this
    /// works the same on every client (own player and remote players alike).
    ///
    /// Player ids are Fusion's PlayerRef.PlayerId as a ulong - the id type every GameEvent payload
    /// (StatDepletedPayload.playerId, etc.) already used. 0 means "no player".
    /// </summary>
    public static class NetworkPlayers
    {
        private static readonly Dictionary<ulong, CorePlayerManager> s_Players = new Dictionary<ulong, CorePlayerManager>();
        private static readonly List<ulong> s_SortedIds = new List<ulong>();

        /// <summary>Raised whenever a player avatar spawns or despawns on this client.</summary>
        public static event Action RosterChanged;

        /// <summary>Raised when this client's own player avatar spawns.</summary>
        public static event Action<CorePlayerManager> LocalPlayerSpawned;

        /// <summary>This client's own player avatar, or null if it hasn't spawned (yet).</summary>
        public static CorePlayerManager Local { get; private set; }

        /// <summary>Ids of every spawned player avatar, in join order (ascending id).</summary>
        public static IReadOnlyList<ulong> Ids => s_SortedIds;

        /// <summary>Every spawned player avatar.</summary>
        public static IEnumerable<CorePlayerManager> All => s_Players.Values;

        public static int Count => s_Players.Count;

        /// <summary>The running gameplay NetworkRunner (null when not in a session).</summary>
        public static NetworkRunner Runner => FusionSessionService.HasInstance ? FusionSessionService.Instance.GameRunner : null;

        /// <summary>This client's player id, or 0 when not in a session.</summary>
        public static ulong LocalClientId
        {
            get
            {
                NetworkRunner runner = Runner;
                return runner != null && runner.IsRunning ? ToClientId(runner.LocalPlayer) : 0;
            }
        }

        public static ulong ToClientId(PlayerRef player) => player.IsRealPlayer ? (ulong)player.PlayerId : 0;

        public static bool TryGet(ulong clientId, out CorePlayerManager player)
        {
            if (s_Players.TryGetValue(clientId, out player) && player != null) return true;
            player = null;
            return false;
        }

        /// <summary>Finds a component on the given player's avatar (e.g. PlayerScore, CorePlayerState).</summary>
        public static bool TryGetComponent<T>(ulong clientId, out T component) where T : class
        {
            component = null;
            return TryGet(clientId, out CorePlayerManager player) && player.TryGetComponent(out component);
        }

        /// <summary>
        /// The player's display name, or "Player N" if they have none (yet) - or if this player has turned
        /// off Show Display Names on the Change Profile screen (your own name is always shown to you).
        /// </summary>
        public static string GetDisplayName(ulong clientId)
        {
            string name = GetSharedName(clientId);
            bool isLocal = clientId != 0 && clientId == LocalClientId;
            if (!string.IsNullOrEmpty(name) && (isLocal || PlayerProfile.ShowDisplayNames)) return name;
            return $"Player {GetPlayerNumber(clientId)}";
        }

        /// <summary>"Player 1: Name", or just "Player 1" when there's no name to show (see <see cref="GetDisplayName"/>).</summary>
        public static string GetPlayerLabel(ulong clientId)
        {
            int number = GetPlayerNumber(clientId);
            string name = GetDisplayName(clientId);
            string numbered = $"Player {number}";
            return name == numbered ? numbered : $"{numbered}: {name}";
        }

        /// <summary>
        /// 1-based position of the player in the session, in join order - the "N" in "Player N". The same
        /// numbering is used in the lobby and in game.
        /// </summary>
        public static int GetPlayerNumber(ulong clientId)
        {
            NetworkRunner runner = Runner;
            if (runner != null && runner.IsRunning)
            {
                int position = 1;
                bool found = false;
                foreach (PlayerRef player in runner.ActivePlayers)
                {
                    if (!player.IsRealPlayer) continue;
                    if ((ulong)player.PlayerId == clientId) found = true;
                    else if ((ulong)player.PlayerId < clientId) position++;
                }
                if (found) return position;
            }

            int index = s_SortedIds.IndexOf(clientId);
            return index >= 0 ? index + 1 : (int)clientId;
        }

        /// <summary>The name the player chose (SessionProfiles), falling back to their avatar's name; "" if none.</summary>
        private static string GetSharedName(ulong clientId)
        {
            if (SessionProfiles.TryGet((int)clientId, out SessionProfile profile) && !string.IsNullOrEmpty(profile.DisplayName))
            {
                return profile.DisplayName;
            }

            if (TryGet(clientId, out CorePlayerManager player))
            {
                string name = NameFilter.Clean(player.PlayerName);
                if (name.Length > 0 && name != CorePlayerManager.UninitializedName && NameFilter.IsAllowed(name)) return name;
            }
            return string.Empty;
        }

        internal static void Register(CorePlayerManager player)
        {
            ulong id = player.OwnerClientId;
            if (id == 0) return;

            s_Players[id] = player;
            if (!s_SortedIds.Contains(id))
            {
                s_SortedIds.Add(id);
                s_SortedIds.Sort();
            }

            // Shows the avatar as the character its player picked (Change Profile screen).
            PlayerCharacterModel.Attach(player);

            if (player.IsOwner)
            {
                Local = player;
                LocalPlayerSpawned?.Invoke(player);
            }
            RosterChanged?.Invoke();
        }

        internal static void Unregister(CorePlayerManager player)
        {
            ulong removedId = 0;
            foreach (KeyValuePair<ulong, CorePlayerManager> pair in s_Players)
            {
                if (pair.Value == player)
                {
                    removedId = pair.Key;
                    break;
                }
            }

            if (removedId != 0)
            {
                s_Players.Remove(removedId);
                s_SortedIds.Remove(removedId);
            }

            if (Local == player) Local = null;
            RosterChanged?.Invoke();
        }
    }
}
