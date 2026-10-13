using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>Listens for the Interact button next to a dinosaur (added to the map by <see cref="Dinosaurs"/>).</summary>
    public class DinosaurInteract : MonoBehaviour
    {
        private InputBindingsData m_Bindings;

        private void Awake()
        {
            m_Bindings = InputBindingsStore.Load();
            InputBindingsStore.Saved += ReloadBindings; // rebound mid-game
        }

        private void OnDestroy()
        {
            InputBindingsStore.Saved -= ReloadBindings;
        }

        private void ReloadBindings() => m_Bindings = InputBindingsStore.Load();

        private void Update()
        {
            if (InGameMenu.IsOpen || m_Bindings == null) return;
            if (!BoundControls.WasPressed(m_Bindings.Keyboard.Interact, m_Bindings.Gamepad.Interact)) return;

            CorePlayerManager local = NetworkPlayers.Local;
            if (local == null) return;

            // Middle of the body rather than the feet, so a dinosaur's bounding box is reachable from either side.
            Vector3 point = local.transform.position + Vector3.up * 0.9f;
            int index = Dinosaurs.FindNear(point);
            if (index >= 0) Dinosaurs.RequestSound(index);
        }
    }
}
