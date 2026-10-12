using System;
using System.Collections.Generic;
using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>What every player in the session knows about another player: their display name and character.</summary>
    public struct SessionProfile
    {
        public string DisplayName;
        public string CharacterId;
    }

    /// <summary>
    /// The display name and character of every player in the current session, including this one - shared
    /// as soon as a session starts (lobby or gameplay), before anyone has an avatar. Each client sends its
    /// own <see cref="PlayerProfile"/> to everyone with a static Fusion RPC (<see cref="PlayerProfileSync"/>)
    /// when it starts, and again whenever someone joins so the newcomer hears from everyone.
    /// FusionSessionService drives the sends and clears the list when the session ends.
    /// </summary>
    public static class SessionProfiles
    {
        private static readonly Dictionary<int, SessionProfile> s_Profiles = new Dictionary<int, SessionProfile>();

        /// <summary>Raised whenever a player's profile arrives or changes, or a player leaves.</summary>
        public static event Action Changed;

        public static bool TryGet(int playerId, out SessionProfile profile) => s_Profiles.TryGetValue(playerId, out profile);

        /// <summary>Sends this player's saved profile to everyone in the session (and records it locally).</summary>
        public static void BroadcastLocal(NetworkRunner runner)
        {
            if (runner == null || !runner.IsRunning || !runner.LocalPlayer.IsRealPlayer) return;

            string name = PlayerProfile.DisplayName;
            string character = PlayerProfile.CharacterId;
            Set(runner.LocalPlayer.PlayerId, name, character);
            PlayerProfileSync.ProfileRpc(runner, name, character);
        }

        internal static void Set(int playerId, string displayName, string characterId)
        {
            string name = NameFilter.Clean(displayName);
            if (!NameFilter.IsAllowed(name)) name = string.Empty; // a modified client - show them as "Player N"

            var profile = new SessionProfile { DisplayName = name, CharacterId = characterId ?? string.Empty };
            if (s_Profiles.TryGetValue(playerId, out SessionProfile existing) &&
                existing.DisplayName == profile.DisplayName && existing.CharacterId == profile.CharacterId)
            {
                return;
            }

            s_Profiles[playerId] = profile;
            Changed?.Invoke();
        }

        internal static void Remove(int playerId)
        {
            if (s_Profiles.Remove(playerId)) Changed?.Invoke();
        }

        internal static void Clear()
        {
            if (s_Profiles.Count == 0) return;
            s_Profiles.Clear();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Holds the static RPC that carries profiles (static RPCs need a SimulationBehaviour to live on, but
    /// no instance or NetworkObject). Never added to anything.
    /// </summary>
    public class PlayerProfileSync : SimulationBehaviour
    {
        [Rpc]
        public static void ProfileRpc(NetworkRunner runner, string displayName, string characterId, RpcInfo info = default)
        {
            PlayerRef source = info.Source.IsRealPlayer ? info.Source : runner.LocalPlayer;
            if (!source.IsRealPlayer) return;
            SessionProfiles.Set(source.PlayerId, displayName, characterId);
        }
    }
}
