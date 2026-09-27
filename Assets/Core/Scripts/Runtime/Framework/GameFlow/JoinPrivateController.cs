using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Join Game > Private: a room-code text box in the middle of the screen, with Join and Back in the
    /// bottom-right column. Enter in the text box also joins; B/Esc goes back. Joining isn't wired up yet
    /// (it'll join the Fusion 2 session whose name is the code) - Join just reports that.
    ///
    /// Navigation is explicit (the text box is centered and the buttons are bottom-right, so UI Toolkit's
    /// nearest-neighbor navigation isn't reliable between them): Down from the text box goes to Join,
    /// Join's Up/Left and Back's Left return to the text box, and Join/Back move Up/Down between each other.
    /// Left/Right inside the text box are left for moving the caret.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class JoinPrivateController : MonoBehaviour
    {
        [Tooltip("Same logo as the other GameFlow screens.")]
        [SerializeField] private Texture2D titleImage;

        [Tooltip("Scene to load when Back (or B/Esc) is pressed.")]
        [SerializeField] private string backSceneName = "JoinGame";

        [SerializeField] private AudioClip selectSound;
        [SerializeField] private AudioClip cancelSound;

        private const int MaxCodeLength = 16;

        private TextField m_CodeField;
        private Label m_StatusLabel;
        private Button m_JoinButton;
        private Button m_BackButton;

        private void Awake()
        {
            GamepadUIBindingFix.Apply();

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            root.style.flexGrow = 1;
            root.style.backgroundColor = Color.black;
            MenuUI.AddSmallLogo(root, titleImage);

            var center = new VisualElement();
            center.style.position = Position.Absolute;
            center.style.left = Length.Percent(50);
            center.style.top = Length.Percent(50);
            center.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
            center.style.alignItems = Align.Center;
            root.Add(center);

            var prompt = new Label("ENTER ROOM CODE");
            prompt.style.color = Color.white;
            prompt.style.fontSize = 20;
            prompt.style.marginBottom = 12;
            center.Add(prompt);

            m_CodeField = new TextField { maxLength = MaxCodeLength };
            m_CodeField.style.width = 360;
            m_CodeField.style.fontSize = 24;
            center.Add(m_CodeField);

            m_StatusLabel = new Label("");
            m_StatusLabel.style.color = new Color(0.8f, 0.8f, 0.8f);
            m_StatusLabel.style.fontSize = 16;
            m_StatusLabel.style.marginTop = 12;
            center.Add(m_StatusLabel);

            VisualElement column = MenuUI.AddBottomRightColumn(root);
            m_JoinButton = MenuUI.MakeButton("Join", OnJoin, selectSound);
            m_BackButton = MenuUI.MakeButton("Back", OnBack, cancelSound);
            column.Add(m_JoinButton);
            column.Add(m_BackButton);

            // Enter in the text box = Join.
            m_CodeField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    evt.StopPropagation();
                    MenuUI.PlaySound(selectSound);
                    OnJoin();
                }
            }, TrickleDown.TrickleDown);

            m_CodeField.RegisterCallback<NavigationMoveEvent>(evt =>
            {
                // PreventDefault stops UI Toolkit's own focus navigation in every direction; the caret's
                // Left/Right movement comes from key handling, not this event, so it's unaffected.
                evt.PreventDefault();
                if (evt.direction == NavigationMoveEvent.Direction.Down)
                {
                    evt.StopPropagation();
                    m_JoinButton.Focus();
                }
            }, TrickleDown.TrickleDown);

            m_JoinButton.RegisterCallback<NavigationMoveEvent>(evt =>
            {
                Suppress(evt);
                if (evt.direction == NavigationMoveEvent.Direction.Up || evt.direction == NavigationMoveEvent.Direction.Left)
                    m_CodeField.Focus();
                else if (evt.direction == NavigationMoveEvent.Direction.Down)
                    m_BackButton.Focus();
            });

            m_BackButton.RegisterCallback<NavigationMoveEvent>(evt =>
            {
                Suppress(evt);
                if (evt.direction == NavigationMoveEvent.Direction.Up)
                    m_JoinButton.Focus();
                else if (evt.direction == NavigationMoveEvent.Direction.Left)
                    m_CodeField.Focus();
            });

            MenuUI.RegisterCancelAsBack(root, cancelSound, OnBack);
            m_CodeField.Focus();
        }

        private static void Suppress(NavigationMoveEvent evt)
        {
            evt.PreventDefault();
            evt.StopPropagation();
        }

        private void OnJoin()
        {
            string code = m_CodeField.value?.Trim() ?? "";
            if (code.Length == 0)
            {
                m_StatusLabel.text = "Enter a room code first.";
                return;
            }

            // Placeholder until joining is wired up (Fusion 2: StartGame with SessionName = code).
            m_StatusLabel.text = "Joining private games isn't hooked up yet.";
            Debug.Log($"[JoinPrivate] Join room code '{code}' (not implemented yet).");
        }

        private void OnBack() => SceneManager.LoadScene(backSceneName);
    }
}
