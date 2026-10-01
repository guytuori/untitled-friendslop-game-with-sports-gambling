using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Single Player menu, reached from the main menu: Change Profile (see ChangeProfileController),
    /// How To Play, Practice Map, Practice Challenges, and Back. Same look as the Join Game menu - full
    /// logo with the buttons in the bottom-right corner. B/Esc also goes back to the main menu. A single
    /// vertical column, so UI Toolkit's own Up/Down navigation is enough here.
    ///
    /// How To Play, Practice Map and Practice Challenges are placeholders for now (they just log).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class SinglePlayerController : MonoBehaviour
    {
        [Tooltip("Same logo as the other GameFlow screens.")]
        [SerializeField] private Texture2D titleImage;

        [Tooltip("Scene to load when Change Profile is clicked.")]
        [SerializeField] private string changeProfileSceneName = "ChangeProfile";

        [Tooltip("Scene to load when Back (or B/Esc) is pressed.")]
        [SerializeField] private string backSceneName = "MainMenu";

        [SerializeField] private AudioClip selectSound;
        [SerializeField] private AudioClip cancelSound;

        private void Awake()
        {
            GamepadUIBindingFix.Apply();

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            MenuUI.SetupRoot(root);
            MenuUI.AddFullLogo(root, titleImage);

            VisualElement column = MenuUI.AddBottomRightColumn(root);
            Button changeProfileButton = MenuUI.MakeButton("Change Profile", () => SceneManager.LoadScene(changeProfileSceneName), selectSound);
            column.Add(changeProfileButton);
            column.Add(MenuUI.MakeButton("How To Play", () => NotImplemented("How To Play"), selectSound));
            column.Add(MenuUI.MakeButton("Practice Map", () => NotImplemented("Practice Map"), selectSound));
            column.Add(MenuUI.MakeButton("Practice Challenges", () => NotImplemented("Practice Challenges"), selectSound));
            column.Add(MenuUI.MakeButton("Back", OnBack, cancelSound));

            MenuUI.RegisterCancelAsBack(root, cancelSound, OnBack);
            MenuUI.FocusFirst(changeProfileButton);
        }

        private static void NotImplemented(string option)
        {
            Debug.Log($"[SinglePlayer] {option} selected (not implemented yet).");
        }

        private void OnBack() => SceneManager.LoadScene(backSceneName);
    }
}
