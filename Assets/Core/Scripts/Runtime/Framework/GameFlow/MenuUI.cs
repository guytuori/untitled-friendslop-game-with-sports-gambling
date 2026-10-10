using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Shared building blocks for the simple GameFlow menu screens added with the Join Game flow
    /// (JoinGameController, JoinPublicController, JoinPrivateController, GameBrowserController): the
    /// same black background, full-size logo, bottom-right button column, bright-pill focus styling and
    /// click sounds that MainMenuController/SettingsController each build inline.
    /// </summary>
    public static class MenuUI
    {
        public static readonly Color FocusedBackground = new Color(0.92f, 0.92f, 0.92f);
        public static readonly Color FocusedText = Color.black;
        public static readonly Color UnfocusedBackground = new Color(0.12f, 0.12f, 0.12f);
        public static readonly Color UnfocusedText = new Color(0.5f, 0.5f, 0.5f);

        public static void SetupRoot(VisualElement root)
        {
            root.style.flexGrow = 1;
            root.style.backgroundColor = Color.black;
            root.style.justifyContent = Justify.Center;
            root.style.alignItems = Align.Center;
        }

        /// <summary>Same 99%/99% logo box as the title screen, main menu and Settings.</summary>
        public static void AddFullLogo(VisualElement root, Texture2D titleImage)
        {
            if (titleImage == null) return;

            var title = new Image { image = titleImage, scaleMode = ScaleMode.ScaleToFit };
            title.style.maxWidth = Length.Percent(99);
            title.style.maxHeight = Length.Percent(99);
            title.style.width = Length.Percent(99);
            title.style.height = Length.Percent(99);
            root.Add(title);
        }

        /// <summary>Same small top-center logo as the Audio/Graphics/Change Keybindings screens.</summary>
        public static void AddSmallLogo(VisualElement root, Texture2D titleImage)
        {
            if (titleImage == null) return;

            var title = new Image { image = titleImage, scaleMode = ScaleMode.ScaleToFit };
            title.style.position = Position.Absolute;
            title.style.top = 20;
            title.style.left = Length.Percent(50);
            title.style.translate = new Translate(Length.Percent(-50), 0);
            title.style.width = Length.Percent(22);
            title.style.height = Length.Percent(22);
            root.Add(title);
        }

        /// <summary>The absolutely-positioned bottom-right button column every GameFlow menu uses.</summary>
        public static VisualElement AddBottomRightColumn(VisualElement root)
        {
            var column = new VisualElement();
            column.style.position = Position.Absolute;
            column.style.right = 40;
            column.style.bottom = 40;
            column.style.flexDirection = FlexDirection.Column;
            column.style.alignItems = Align.FlexEnd;
            root.Add(column);
            return column;
        }

        public static Button MakeButton(string text, Action onClick, AudioClip clickSound, float width = 200f)
        {
            var button = new Button(() =>
            {
                PlaySound(clickSound);
                onClick();
            })
            { text = text };
            ApplyButtonFont(button);
            button.style.width = width;
            button.style.height = 40;
            button.style.marginTop = 8;
            button.style.marginBottom = 8;
            button.style.fontSize = 18;

            SetButtonFocusedVisual(button, false);
            button.RegisterCallback<FocusInEvent>(_ => SetButtonFocusedVisual(button, true));
            button.RegisterCallback<FocusOutEvent>(_ => SetButtonFocusedVisual(button, false));
            return button;
        }

        /// <summary>
        /// Gives a button the game font straight away. <see cref="GameFont"/> puts it on every screen anyway;
        /// this just makes sure a button has it from its very first frame.
        /// </summary>
        public static void ApplyButtonFont(VisualElement element) => GameFont.Apply(element);

        public static void SetButtonFocusedVisual(Button button, bool focused)
        {
            button.style.backgroundColor = focused ? FocusedBackground : UnfocusedBackground;
            button.style.color = focused ? FocusedText : UnfocusedText;
        }

        public static void PlaySound(AudioClip clip)
        {
            if (clip != null)
            {
                AudioVolumeService.PlayOneShot(clip, AudioCategory.UISoundEffects, Vector3.zero);
            }
        }

        /// <summary>
        /// B (gamepad Cancel) / Esc anywhere on the screen = Back. A bubble-phase handler on the root, so
        /// anything focused that handles Cancel itself (and stops propagation) takes priority.
        /// </summary>
        public static void RegisterCancelAsBack(VisualElement root, AudioClip cancelSound, Action back)
        {
            root.RegisterCallback<NavigationCancelEvent>(evt =>
            {
                evt.StopPropagation();
                PlaySound(cancelSound);
                back();
            });
        }

        /// <summary>Focuses a button and applies its focused styling immediately (FocusInEvent may not have fired yet on the first frame).</summary>
        public static void FocusFirst(Button button)
        {
            button.Focus();
            SetButtonFocusedVisual(button, true);
        }
    }
}
