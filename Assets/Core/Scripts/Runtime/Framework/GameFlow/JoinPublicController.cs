using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Join Game > Public: Normal / Hard / Very Hard search straight away with that search preset
    /// (GameSearchPresets - different defaults from the Host Game presets), Custom opens the full search
    /// filter (the FindGames scene - HostGameController in FindGamesFilter mode), Back returns to the
    /// Join Game menu. B/Esc also goes back. Same look as the Settings menu.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class JoinPublicController : MonoBehaviour
    {
        [Tooltip("Same logo as the other GameFlow screens.")]
        [SerializeField] private Texture2D titleImage;

        [SerializeField] private string gameBrowserSceneName = "GameBrowser";
        [SerializeField] private string customSceneName = "FindGames";

        [Tooltip("Scene to load when Back (or B/Esc) is pressed.")]
        [SerializeField] private string backSceneName = "JoinGame";

        [SerializeField] private AudioClip selectSound;
        [SerializeField] private AudioClip cancelSound;

        private void Awake()
        {
            GamepadUIBindingFix.Apply();

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            MenuUI.SetupRoot(root);
            MenuUI.AddFullLogo(root, titleImage);

            VisualElement column = MenuUI.AddBottomRightColumn(root);
            Button normalButton = MenuUI.MakeButton("Normal", () => Search(GameSearchPresets.Normal), selectSound);
            column.Add(normalButton);
            column.Add(MenuUI.MakeButton("Hard", () => Search(GameSearchPresets.Hard), selectSound));
            column.Add(MenuUI.MakeButton("Very Hard", () => Search(GameSearchPresets.VeryHard), selectSound));
            column.Add(MenuUI.MakeButton("Custom", () => SceneManager.LoadScene(customSceneName), selectSound));
            column.Add(MenuUI.MakeButton("Back", OnBack, cancelSound));

            MenuUI.RegisterCancelAsBack(root, cancelSound, OnBack);
            MenuUI.FocusFirst(normalButton);
        }

        private void Search(GameSearchFilter filter)
        {
            GameSearch.CurrentFilter = filter;
            GameSearch.BrowserBackScene = SceneManager.GetActiveScene().name;
            SceneManager.LoadScene(gameBrowserSceneName);
        }

        private void OnBack() => SceneManager.LoadScene(backSceneName);
    }
}
