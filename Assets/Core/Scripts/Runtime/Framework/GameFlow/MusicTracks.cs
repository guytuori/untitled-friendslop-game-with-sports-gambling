using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Which song plays where - the names are the file names in Assets/Core/Audio/Music without the extension.
    /// null = silence there for now. <see cref="MusicPlayer"/> does the playing.
    ///
    ///   - Title screen: Parkour_Predictions, once (no loop).
    ///   - Menus: <see cref="Menu"/>, Parkour_Parley (it takes over from the title song on the first menu).
    ///     Moving between menus doesn't restart it.
    ///   - Course flyover (the intro, every map): Ready_to_Run.
    ///   - The round itself, from PARKOUR!: the map's song (<see cref="ForMap"/>). Fades out at the end of the
    ///     round, under TIME!.
    ///   - Round won (Round Results screen): Banking_on_Victory. Target missed (Final Score): <see cref="RoundLost"/>.
    ///   - Practice Map: the map's song. Practice Challenges: <see cref="PracticeChallenge"/>.
    /// </summary>
    public static class MusicTracks
    {
        public const string Title = "Parkour_Predictions";
        public const string Flyover = "Ready_to_Run";
        public const string RoundWon = "Banking_on_Victory";

        /// <summary>The Credits scene (Single Player > Credits). Plays once; CreditsController is timed to it.</summary>
        public const string Credits = "Credits";

        /// <summary>Main menu and the other menu screens.</summary>
        public const string Menu = "Parkour_Parley";

        /// <summary>The Final Score screen after missing the target. Not written yet.</summary>
        public const string RoundLost = null;

        /// <summary>Single Player > Practice Challenges. Not written yet.</summary>
        public const string PracticeChallenge = null;

        /// <summary>The song for a map (its MapCatalog id, e.g. "LastStop"), or null.</summary>
        public static string ForMap(string mapId)
        {
            switch (mapId)
            {
                case "DinosaurZoo": return "Dinosaur_Country";
                case "NinjaPirateTower": return "Of_Ninjas_and_Pirates";
                case "LastStop": return "Last_Stop";
                case "SeaBase": return "Twenty_Thousand_Inches_Over_the_Sea"; // the map isn't built yet
                default: return null;
            }
        }

        /// <summary>The map's catalog id ("LastStop") from the instance MatchLayout made ("Map_LastStop"), or null.</summary>
        public static string MapIdOf(MatchLayout layout)
        {
            GameObject map = layout != null ? layout.MapInstance : null;
            if (map == null) return null;
            string name = map.name;
            return name.StartsWith("Map_", System.StringComparison.Ordinal) ? name.Substring(4) : name;
        }
    }
}
