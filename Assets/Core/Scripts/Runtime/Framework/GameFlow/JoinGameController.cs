using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Join Game menu, reached from the main menu: Public (browse hosted public games - see
    /// JoinPublicController), Private (join by room code - see JoinPrivateController), and Back. Same
    /// look as the Settings menu. B/Esc also goes back. A single vertical column, so UI Toolkit's own
    /// Up/Down navigation is enough here, same as MainMenu and Settings.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class JoinGameController : MonoBehaviour
    {
        [Tooltip("Same logo as the other GameFlow screens.")]
        [SerializeField] private Texture2D titleImage;

        [SerializeField] private string publicSceneName = "JoinPublic";
        [SerializeField] private string privateSceneName = "JoinPrivate";

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
            Button publicButton = MenuUI.MakeButton("Public", () => SceneManager.LoadScene(publicSceneName), selectSound);
            column.Add(publicButton);
            column.Add(MenuUI.MakeButton("Private", () => SceneManager.LoadScene(privateSceneName), selectSound));
            column.Add(MenuUI.MakeButton("Back", OnBack, cancelSound));

            MenuUI.RegisterCancelAsBack(root, cancelSound, OnBack);
            MenuUI.FocusFirst(publicButton);
        }

        private void OnBack() => SceneManager.LoadScene(backSceneName);
    }
}
