using System.Collections.Generic;
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
    /// PRACTICE MAP opens a popup over this screen with the same 2x5 map grid as Host Game / Find Games
    /// (<see cref="maps"/> - thumbnails, names and flavor text): pick a map and Start (A/Enter, the Start
    /// button, or double-click a map) to play it alone with no time limit (see <see cref="PracticeMode"/>).
    /// B/Esc or Back closes the popup. Random picks one of the other maps.
    ///
    /// How To Play and Practice Challenges are placeholders for now (they just log).
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

        [Tooltip("Practice Map's map grid, in grid order (5 per row) - same entries as the Host Game screen's map list. " +
                 "Map Prefab is what gets played (file name in Assets/Core/Prefabs/Maps); leave it empty on Random.")]
        [SerializeField] private HostGameController.MapEntry[] maps;

        [SerializeField] private AudioClip selectSound;
        [SerializeField] private AudioClip cancelSound;

        private const int MapColumns = 5;
        private const float ThumbnailWidth = 128f;
        private const float ThumbnailHeight = 72f;
        private static readonly Color SelectedBorder = new Color(1f, 0.85f, 0.2f);
        private static readonly Color SelectedBorderUnfocused = new Color(0.75f, 0.75f, 0.75f);
        private static readonly Color ErrorColor = new Color(1f, 0.55f, 0.45f);

        /// <summary>The map last picked for practice, so the popup reopens on it.</summary>
        private static int s_LastPracticeMap;

        private VisualElement m_Column;
        private Button m_PracticeButton;

        private VisualElement m_Popup;
        private VisualElement m_Grid;
        private readonly List<Image> m_Cells = new List<Image>();
        private Label m_MapNameLabel;
        private Label m_FlavorLabel;
        private Label m_StatusLabel;
        private Button m_StartButton;
        private Button m_PopupBackButton;
        private int m_Selected;
        private bool m_Starting;

        private int MapCount => maps != null ? maps.Length : 0;
        private bool PopupOpen => m_Popup != null && m_Popup.style.display == DisplayStyle.Flex;

        private void Awake()
        {
            GamepadUIBindingFix.Apply();

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            MenuUI.SetupRoot(root);
            MenuUI.AddFullLogo(root, titleImage);

            m_Column = MenuUI.AddBottomRightColumn(root);
            Button changeProfileButton = MenuUI.MakeButton("Change Profile", () => SceneManager.LoadScene(changeProfileSceneName), selectSound);
            m_Column.Add(changeProfileButton);
            m_Column.Add(MenuUI.MakeButton("How To Play", () => NotImplemented("How To Play"), selectSound));
            m_PracticeButton = MenuUI.MakeButton("Practice Map", OpenPracticePopup, selectSound);
            m_Column.Add(m_PracticeButton);
            m_Column.Add(MenuUI.MakeButton("Practice Challenges", () => NotImplemented("Practice Challenges"), selectSound));
            m_Column.Add(MenuUI.MakeButton("Back", OnBack, cancelSound));

            BuildPracticePopup(root);

            MenuUI.RegisterCancelAsBack(root, cancelSound, OnBack);
            MenuUI.FocusFirst(changeProfileButton);
        }

        private static void NotImplemented(string option)
        {
            Debug.Log($"[SinglePlayer] {option} selected (not implemented yet).");
        }

        /// <summary>B/Esc or Back: closes the practice popup if it's open, otherwise back to the main menu.</summary>
        private void OnBack()
        {
            if (PopupOpen)
            {
                ClosePracticePopup();
                return;
            }
            SceneManager.LoadScene(backSceneName);
        }

        // =====================================================================================
        // Practice Map popup
        // =====================================================================================

        private void BuildPracticePopup(VisualElement root)
        {
            m_Popup = new VisualElement();
            m_Popup.style.position = Position.Absolute;
            m_Popup.style.left = 0;
            m_Popup.style.right = 0;
            m_Popup.style.top = 0;
            m_Popup.style.bottom = 0;
            m_Popup.style.backgroundColor = new Color(0f, 0f, 0f, 0.8f);
            m_Popup.style.justifyContent = Justify.Center;
            m_Popup.style.alignItems = Align.Center;
            m_Popup.style.display = DisplayStyle.None;
            root.Add(m_Popup);

            var panel = new VisualElement();
            panel.style.backgroundColor = new Color(0.09f, 0.09f, 0.09f);
            SetBorder(panel, 2f, new Color(0.35f, 0.35f, 0.35f));
            panel.style.borderTopLeftRadius = 8;
            panel.style.borderTopRightRadius = 8;
            panel.style.borderBottomLeftRadius = 8;
            panel.style.borderBottomRightRadius = 8;
            panel.style.paddingLeft = 24;
            panel.style.paddingRight = 24;
            panel.style.paddingTop = 18;
            panel.style.paddingBottom = 18;
            panel.style.alignItems = Align.Center;
            m_Popup.Add(panel);

            var title = new Label("PRACTICE MAP");
            title.style.fontSize = 26;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            title.style.marginBottom = 4;
            panel.Add(title);

            var subtitle = new Label("Run the map on your own - no time limit, no wagers. Pickups still score.");
            subtitle.style.fontSize = 14;
            subtitle.style.color = new Color(0.7f, 0.7f, 0.7f);
            subtitle.style.marginBottom = 12;
            panel.Add(subtitle);

            // The grid: explicit rows of MapColumns cells (same reason as Host Game - wrapping depends on exact widths).
            m_Grid = new VisualElement { focusable = true };
            m_Grid.style.flexDirection = FlexDirection.Column;
            m_Grid.style.paddingLeft = 4;
            m_Grid.style.paddingRight = 4;
            m_Grid.style.paddingTop = 4;
            m_Grid.style.paddingBottom = 4;
            panel.Add(m_Grid);

            VisualElement gridRow = null;
            for (int i = 0; i < MapCount; i++)
            {
                if (i % MapColumns == 0)
                {
                    gridRow = new VisualElement();
                    gridRow.style.flexDirection = FlexDirection.Row;
                    m_Grid.Add(gridRow);
                }

                int index = i;
                var cell = new Image { image = maps[i].thumbnail, scaleMode = ScaleMode.ScaleToFit };
                cell.style.width = ThumbnailWidth;
                cell.style.height = ThumbnailHeight;
                cell.style.flexShrink = 0;
                cell.style.marginLeft = 4;
                cell.style.marginRight = 4;
                cell.style.marginTop = 4;
                cell.style.marginBottom = 4;
                SetBorder(cell, 3f, Color.clear);
                cell.RegisterCallback<ClickEvent>(evt =>
                {
                    if (m_Starting) return;
                    if (index != m_Selected)
                    {
                        Select(index);
                        MenuUI.PlaySound(selectSound);
                    }
                    if (evt.clickCount >= 2) StartPractice();
                });
                gridRow.Add(cell);
                m_Cells.Add(cell);
            }

            m_MapNameLabel = new Label("");
            m_MapNameLabel.style.fontSize = 22;
            m_MapNameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            m_MapNameLabel.style.color = Color.white;
            m_MapNameLabel.style.marginTop = 10;
            panel.Add(m_MapNameLabel);

            m_FlavorLabel = new Label("");
            m_FlavorLabel.style.fontSize = 15;
            m_FlavorLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            m_FlavorLabel.style.color = new Color(0.8f, 0.8f, 0.8f);
            m_FlavorLabel.style.whiteSpace = WhiteSpace.Normal;
            m_FlavorLabel.style.maxWidth = MapColumns * (ThumbnailWidth + 14f);
            m_FlavorLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_FlavorLabel.style.minHeight = 40;
            panel.Add(m_FlavorLabel);

            m_StatusLabel = new Label("");
            m_StatusLabel.style.fontSize = 15;
            m_StatusLabel.style.color = ErrorColor;
            m_StatusLabel.style.minHeight = 22;
            m_StatusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            panel.Add(m_StatusLabel);

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.marginTop = 4;
            panel.Add(buttons);

            m_StartButton = MenuUI.MakeButton("Start", StartPractice, selectSound, 180f);
            m_StartButton.style.marginRight = 12;
            buttons.Add(m_StartButton);
            m_PopupBackButton = MenuUI.MakeButton("Back", ClosePracticePopup, cancelSound, 180f);
            buttons.Add(m_PopupBackButton);

            // Grid: arrows / stick move the selection; A/Enter starts; down from the bottom row goes to Start.
            m_Grid.RegisterCallback<NavigationMoveEvent>(OnGridMove, TrickleDown.TrickleDown);
            m_Grid.RegisterCallback<NavigationSubmitEvent>(evt =>
            {
                evt.StopPropagation();
                MenuUI.PlaySound(selectSound);
                StartPractice();
            }, TrickleDown.TrickleDown);
            m_Grid.RegisterCallback<FocusInEvent>(_ => RefreshCells());
            m_Grid.RegisterCallback<FocusOutEvent>(_ => RefreshCells());

            // Up from either button goes back to the grid.
            foreach (Button b in new[] { m_StartButton, m_PopupBackButton })
            {
                b.RegisterCallback<NavigationMoveEvent>(evt =>
                {
                    if (evt.direction != NavigationMoveEvent.Direction.Up) return;
                    evt.StopPropagation();
                    m_Grid.Focus();
                }, TrickleDown.TrickleDown);
            }
        }

        private void OpenPracticePopup()
        {
            if (MapCount == 0)
            {
                Debug.LogWarning("[SinglePlayer] Practice Map has no maps - fill in the Maps list on SinglePlayerController (SinglePlayer scene).", this);
                return;
            }

            m_Starting = false;
            m_StatusLabel.text = "";
            SetPopupInteractable(true);
            m_Column.SetEnabled(false); // the menu behind the popup can't be reached
            m_Popup.style.display = DisplayStyle.Flex;
            Select(Mathf.Clamp(s_LastPracticeMap, 0, MapCount - 1));
            m_Grid.Focus();
        }

        private void ClosePracticePopup()
        {
            if (m_Starting) return; // can't back out while the session is starting
            m_Popup.style.display = DisplayStyle.None;
            m_Column.SetEnabled(true);
            MenuUI.FocusFirst(m_PracticeButton);
        }

        private void OnGridMove(NavigationMoveEvent evt)
        {
            evt.StopPropagation();
            evt.PreventDefault();
            if (m_Starting || MapCount == 0) return;

            int row = m_Selected / MapColumns;
            int col = m_Selected % MapColumns;
            int lastRow = (MapCount - 1) / MapColumns;

            switch (evt.direction)
            {
                case NavigationMoveEvent.Direction.Left: col = Mathf.Max(0, col - 1); break;
                case NavigationMoveEvent.Direction.Right: col = Mathf.Min(MapColumns - 1, col + 1); break;
                case NavigationMoveEvent.Direction.Up: row = Mathf.Max(0, row - 1); break;
                case NavigationMoveEvent.Direction.Down:
                    if (row >= lastRow)
                    {
                        MenuUI.FocusFirst(m_StartButton);
                        return;
                    }
                    row++;
                    break;
                default: return;
            }

            int next = Mathf.Min(row * MapColumns + col, MapCount - 1);
            if (next != m_Selected)
            {
                Select(next);
                MenuUI.PlaySound(selectSound);
            }
        }

        private void Select(int index)
        {
            m_Selected = Mathf.Clamp(index, 0, Mathf.Max(0, MapCount - 1));
            s_LastPracticeMap = m_Selected;
            RefreshCells();

            HostGameController.MapEntry entry = MapCount > 0 ? maps[m_Selected] : null;
            m_MapNameLabel.text = entry != null ? entry.displayName : "";
            m_FlavorLabel.text = entry != null ? entry.flavorText : "";
            m_StatusLabel.text = "";
        }

        private void RefreshCells()
        {
            bool gridFocused = m_Grid != null && m_Grid.focusController != null && m_Grid.focusController.focusedElement == m_Grid;
            for (int i = 0; i < m_Cells.Count; i++)
            {
                bool selected = i == m_Selected;
                m_Cells[i].style.opacity = selected ? 1f : 0.35f;
                SetBorderColor(m_Cells[i], selected ? (gridFocused ? SelectedBorder : SelectedBorderUnfocused) : Color.clear);
            }
        }

        private async void StartPractice()
        {
            if (m_Starting || MapCount == 0) return;

            HostGameController.MapEntry entry = maps[m_Selected];
            string mapPrefab = ResolveMapPrefab(m_Selected);
            if (string.IsNullOrEmpty(mapPrefab))
            {
                m_StatusLabel.style.color = ErrorColor;
                m_StatusLabel.text = "That map isn't set up yet.";
                return;
            }

            m_Starting = true;
            SetPopupInteractable(false);
            m_StatusLabel.style.color = new Color(0.8f, 0.8f, 0.8f);
            m_StatusLabel.text = "Starting practice...";

            string error = await FusionSessionService.Instance.StartPracticeAsync(mapPrefab, entry.displayName);
            if (this == null) return; // the gameplay scene has already replaced this one

            if (error != null)
            {
                m_Starting = false;
                SetPopupInteractable(true);
                m_StatusLabel.style.color = ErrorColor;
                m_StatusLabel.text = error;
                m_Grid.Focus();
            }
            // On success Fusion loads the gameplay scene.
        }

        /// <summary>The entry's own map prefab, or - for Random (no prefab) - a random pick among the others.</summary>
        private string ResolveMapPrefab(int index)
        {
            if (index >= 0 && index < MapCount && !string.IsNullOrWhiteSpace(maps[index].mapPrefab))
            {
                return maps[index].mapPrefab.Trim();
            }

            var choices = new List<string>();
            foreach (HostGameController.MapEntry e in maps)
            {
                if (e != null && !string.IsNullOrWhiteSpace(e.mapPrefab)) choices.Add(e.mapPrefab.Trim());
            }
            return choices.Count > 0 ? choices[Random.Range(0, choices.Count)] : "";
        }

        private void SetPopupInteractable(bool interactable)
        {
            m_Grid.SetEnabled(interactable);
            m_StartButton.SetEnabled(interactable);
            m_PopupBackButton.SetEnabled(interactable);
        }

        private static void SetBorder(VisualElement element, float width, Color color)
        {
            element.style.borderTopWidth = width;
            element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = width;
            element.style.borderRightWidth = width;
            SetBorderColor(element, color);
        }

        private static void SetBorderColor(VisualElement element, Color color)
        {
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
        }
    }
}
