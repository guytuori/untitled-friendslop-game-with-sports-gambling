using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Marks a Transform as a valid location for a player to spawn or respawn at. Purely a positional
    /// (and rotational) marker - no fields or logic of its own. <see cref="GameManager"/> finds every
    /// PlayerSpawnPoint in the scene at startup (see its RefreshSpawnPointsFromMarkers) and picks
    /// randomly among them each time a player needs a spawn position, for both the very first spawn and
    /// every later respawn.
    ///
    /// Created automatically by MapObstacleSetup for every connected component of "player_spawn_point"
    /// map geometry (one marker per physically separate spawn tile/cluster, positioned at that
    /// component's centroid) - see that class's BuildSpawnPoint. MapColliderSetup separately strips
    /// collision from and hides the "player_spawn_point" tiles themselves (see its
    /// PlayerSpawnPointKeyword handling), so the tile a marker sits on is invisible and non-solid; the
    /// marker's own position is deliberately left exactly where the map places it (typically a little
    /// above the true floor, matching how these tiles are authored) rather than being snapped to the
    /// ground, so a spawned player drops the small remaining distance under normal gravity instead of
    /// popping straight onto the floor.
    ///
    /// Can also be added by hand to any other Transform (e.g. a hand-placed test spawn) to make it a
    /// valid spawn point without going through the map-geometry pipeline.
    /// </summary>
    public class PlayerSpawnPoint : MonoBehaviour
    {
    }
}
