using System;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// This player's own profile, edited on the Change Profile screen and saved in PlayerPrefs:
    ///   - <see cref="DisplayName"/>: shown to everyone in the lobby and in game ("Player 1: name").
    ///   - <see cref="ShowDisplayNames"/>: a local viewing preference - off shows other players only as
    ///     "Player 1", "Player 2", ... (your own name is always shown to you).
    ///   - <see cref="CharacterId"/>: which character model you play as - a <see cref="CharacterCatalog"/>
    ///     entry id. Empty means "whatever the player prefab already uses".
    /// Display name and character are shared with the other players in a session through SessionProfiles.
    /// </summary>
    public static class PlayerProfile
    {
        public const string DefaultDisplayName = "Parkour Player";

        private const string DisplayNameKey = "Profile.DisplayName";
        private const string ShowDisplayNamesKey = "Profile.ShowDisplayNames";
        private const string CharacterIdKey = "Profile.CharacterId";

        /// <summary>Raised after <see cref="Save"/>.</summary>
        public static event Action Changed;

        /// <summary>The saved display name, or <see cref="DefaultDisplayName"/> if none (or a disallowed one) is saved.</summary>
        public static string DisplayName
        {
            get
            {
                string name = NameFilter.Clean(PlayerPrefs.GetString(DisplayNameKey, DefaultDisplayName));
                return NameFilter.Validate(name) == null ? name : DefaultDisplayName;
            }
        }

        public static bool ShowDisplayNames => PlayerPrefs.GetInt(ShowDisplayNamesKey, 1) != 0;

        public static string CharacterId => PlayerPrefs.GetString(CharacterIdKey, string.Empty);

        /// <summary>
        /// Saves the profile. The display name must already have passed <see cref="NameFilter.Validate"/>.
        /// </summary>
        public static void Save(string displayName, bool showDisplayNames, string characterId)
        {
            PlayerPrefs.SetString(DisplayNameKey, NameFilter.Clean(displayName));
            PlayerPrefs.SetInt(ShowDisplayNamesKey, showDisplayNames ? 1 : 0);
            PlayerPrefs.SetString(CharacterIdKey, characterId ?? string.Empty);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }
    }
}
