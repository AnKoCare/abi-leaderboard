using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AbiGames.Leaderboard.UI
{
    /// <summary>
    /// Dựng bằng code một panel bảng xếp hạng chạy được ngay: tab World / Country, mỗi tab một
    /// <see cref="LeaderboardBoardView"/> (danh sách ảo + ô "hạng của tôi" + trạng thái), cụm nút
    /// nhảy. Dùng cho menu <c>Tools/AbiGames/Leaderboard/Create Sample Panel</c>, bản demo và test.
    ///
    /// Panel dùng giao diện tối giản, không sprite, chữ TextMeshPro với font mặc định của project
    /// (cần TMP Essential Resources). Game thường chỉ lấy cấu trúc rồi thay ô / màu theo phong cách riêng.
    /// Kích thước theo màn hình dọc tham chiếu 1080×1920.
    /// </summary>
    public static class LeaderboardPanelFactory
    {
        public sealed class Panel
        {
            public RectTransform Root;
            public LeaderboardTabsView Tabs;
            public LeaderboardBoardView WorldView;
            public LeaderboardBoardView CountryView;
            public LeaderboardScrollJumpButtons JumpButtons;
        }

        public const float CellHeight = 140f;

        private static readonly Color PanelColor = new Color(0.09f, 0.11f, 0.17f, 0.97f);
        private static readonly Color CellColor = new Color(0.17f, 0.2f, 0.29f, 1f);
        private static readonly Color SelfColor = new Color(0.22f, 0.27f, 0.42f, 1f);
        private static readonly Color AccentColor = new Color(1f, 0.78f, 0.25f, 1f);
        private static readonly Color MutedText = new Color(0.68f, 0.72f, 0.8f, 1f);

        /// <summary>Font của lượt dựng đang chạy (null = font mặc định trong TMP Settings). Chỉ dùng trên main thread.</summary>
        private static TMP_FontAsset buildFont;

        /// <summary>Canvas màn hình dọc 1080×1920 (+ EventSystem nếu scene chưa có).</summary>
        public static Canvas CreateCanvas(string name = "LeaderboardCanvas")
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;
            EnsureEventSystem();
            return canvas;
        }

        /// <summary>EventSystem với input module hợp với project (Input System mới nếu có).</summary>
        public static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null)
                return;
            GameObject go = new GameObject("EventSystem", typeof(EventSystem));
            Type inputSystemModule = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (inputSystemModule != null)
                go.AddComponent(inputSystemModule);
            else
                go.AddComponent<StandaloneInputModule>();
        }

        /// <summary>Dựng panel kéo giãn theo <paramref name="parent"/> (phải nằm dưới một Canvas).</summary>
        /// <param name="font">Font cho mọi chữ trong panel. Null = font mặc định của TMP Settings — nên truyền
        /// font của game khi font mặc định không có đủ ký tự (vd. tiếng Việt).</param>
        public static Panel CreatePanel(Transform parent, TMP_FontAsset font = null)
        {
            TMP_FontAsset previous = buildFont;
            buildFont = font;
            try
            {
                return BuildPanel(parent);
            }
            finally
            {
                buildFont = previous;
            }
        }

        private static Panel BuildPanel(Transform parent)
        {
            RectTransform root = NewRect("LeaderboardPanel", parent);
            // Dựng lúc TẮT để OnEnable của các view không chạy khi chưa gắn đủ tham chiếu.
            root.gameObject.SetActive(false);
            Stretch(root, Vector2.zero, Vector2.zero);
            AddImage(root.gameObject, PanelColor);

            TMP_Text title = AddText(root, "Title", "Leaderboard", 64f, TextAlignmentOptions.Center, Color.white);
            title.fontStyle = FontStyles.Bold;
            Anchor(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -150f), new Vector2(0f, -40f));

            RectTransform tabBar = NewRect("Tabs", root);
            Anchor(tabBar, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(40f, -280f), new Vector2(-40f, -170f));
            (Button worldButton, GameObject worldSelected) = CreateTabButton(tabBar, "World", 0f, 0.5f);
            (Button countryButton, GameObject countrySelected) = CreateTabButton(tabBar, "Country", 0.5f, 1f);

            RectTransform body = NewRect("Body", root);
            Anchor(body, Vector2.zero, Vector2.one, new Vector2(0f, 20f), new Vector2(0f, -300f));

            LeaderboardBoardView worldView = CreateBoardView(body, "WorldBoard");
            LeaderboardBoardView countryView = CreateBoardView(body, "CountryBoard");
            // Cả hai bảng tắt — LeaderboardTabsView bật đúng bảng của tab đang chọn, để bảng
            // không tự tải ở OnEnable rồi lại bị tab tải thêm lần nữa.
            worldView.gameObject.SetActive(false);
            countryView.gameObject.SetActive(false);

            GameObject countryLocked = AddText(body, "CountryLocked", "Locked", 44f, TextAlignmentOptions.Center, MutedText).gameObject;
            Stretch((RectTransform)countryLocked.transform, Vector2.zero, Vector2.zero);
            countryLocked.SetActive(false);

            LeaderboardScrollJumpButtons jump = CreateJumpButtons(body);

            LeaderboardTabsView tabs = root.gameObject.AddComponent<LeaderboardTabsView>();
            tabs.Wire(new List<LeaderboardTab>
            {
                new LeaderboardTab { Button = worldButton, SelectedIndicator = worldSelected, View = worldView, Kind = LeaderboardTabKind.World },
                new LeaderboardTab { Button = countryButton, SelectedIndicator = countrySelected, View = countryView, Kind = LeaderboardTabKind.PlayerCountry, LockedRoot = countryLocked },
            }, jump);

            root.gameObject.SetActive(true);
            return new Panel { Root = root, Tabs = tabs, WorldView = worldView, CountryView = countryView, JumpButtons = jump };
        }

        /// <summary>Một <see cref="DefaultLeaderboardCell"/> cao <see cref="CellHeight"/>, kéo giãn ngang.</summary>
        public static DefaultLeaderboardCell CreateCell(Transform parent, string name, bool isSelfCell = false, TMP_FontAsset font = null)
        {
            TMP_FontAsset previous = buildFont;
            buildFont = font ?? buildFont;
            try
            {
                return BuildCell(parent, name, isSelfCell);
            }
            finally
            {
                buildFont = previous;
            }
        }

        private static DefaultLeaderboardCell BuildCell(Transform parent, string name, bool isSelfCell)
        {
            RectTransform rect = NewRect(name, parent);
            rect.sizeDelta = new Vector2(0f, CellHeight);
            Image background = AddImage(rect.gameObject, isSelfCell ? SelfColor : CellColor);
            Button button = rect.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = background;

            RectTransform highlight = NewRect("SelfHighlight", rect);
            Stretch(highlight, Vector2.zero, Vector2.zero);
            Image highlightImage = AddImage(highlight.gameObject, new Color(AccentColor.r, AccentColor.g, AccentColor.b, 0.22f));
            highlightImage.raycastTarget = false;
            highlight.gameObject.SetActive(false);

            TMP_Text rank = AddText(rect, "Rank", "1", 44f, TextAlignmentOptions.Center, Color.white);
            rank.fontStyle = FontStyles.Bold;
            Anchor(rank.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(12f, 0f), new Vector2(132f, 0f));

            RectTransform avatarRect = NewRect("Avatar", rect);
            Anchor(avatarRect, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(142f, -50f), new Vector2(242f, 50f));
            Image avatar = AddImage(avatarRect.gameObject, Color.gray);
            avatar.raycastTarget = false;

            TMP_Text playerName = AddText(rect, "Name", "Player", 40f, TextAlignmentOptions.Left, Color.white);
            playerName.overflowMode = TextOverflowModes.Ellipsis;
            Anchor(playerName.rectTransform, new Vector2(0f, 0.45f), new Vector2(1f, 1f), new Vector2(264f, 0f), new Vector2(-250f, -14f));

            TMP_Text country = AddText(rect, "Country", "VN", 28f, TextAlignmentOptions.Left, MutedText);
            Anchor(country.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0.45f), new Vector2(264f, 14f), new Vector2(-250f, 0f));

            TMP_Text score = AddText(rect, "Score", "0", 44f, TextAlignmentOptions.Right, AccentColor);
            score.fontStyle = FontStyles.Bold;
            Anchor(score.rectTransform, new Vector2(1f, 0.4f), new Vector2(1f, 1f), new Vector2(-240f, 0f), new Vector2(-24f, -12f));

            TMP_Text secondary = AddText(rect, "Secondary", "0", 28f, TextAlignmentOptions.Right, MutedText);
            Anchor(secondary.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0.4f), new Vector2(-240f, 12f), new Vector2(-24f, 0f));

            DefaultLeaderboardCell cell = rect.gameObject.AddComponent<DefaultLeaderboardCell>();
            cell.Wire(rank, playerName, score, secondary, avatar, country, highlight.gameObject, button);
            return cell;
        }

        private static LeaderboardBoardView CreateBoardView(Transform parent, string name)
        {
            RectTransform root = NewRect(name, parent);
            Stretch(root, Vector2.zero, Vector2.zero);

            // ScrollRect phía trên, chừa chỗ cho ô "hạng của tôi" ở đáy.
            RectTransform scrollRectTransform = NewRect("Scroll", root);
            Anchor(scrollRectTransform, Vector2.zero, Vector2.one, new Vector2(0f, CellHeight + 40f), Vector2.zero);
            ScrollRect scroll = scrollRectTransform.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Elastic;
            scroll.scrollSensitivity = 40f;

            RectTransform viewport = NewRect("Viewport", scrollRectTransform);
            Stretch(viewport, Vector2.zero, Vector2.zero);
            viewport.gameObject.AddComponent<RectMask2D>();
            // Image trong suốt để vùng trống giữa các dòng vẫn nhận kéo.
            AddImage(viewport.gameObject, new Color(0f, 0f, 0f, 0f));

            RectTransform content = NewRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;
            LeaderboardVirtualList list = content.gameObject.AddComponent<LeaderboardVirtualList>();
            list.Configure(CellHeight, 8f, 10f, 40f, 16f);

            scroll.viewport = viewport;
            scroll.content = content;

            DefaultLeaderboardCell template = BuildCell(root, "CellTemplate", false);
            template.gameObject.SetActive(false);

            DefaultLeaderboardCell self = BuildCell(root, "SelfCell", true);
            RectTransform selfRect = (RectTransform)self.transform;
            Anchor(selfRect, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(16f, 16f), new Vector2(-16f, 16f + CellHeight));
            self.gameObject.SetActive(false);

            GameObject loading = AddText(root, "Loading", "Loading...", 40f, TextAlignmentOptions.Center, MutedText).gameObject;
            Stretch((RectTransform)loading.transform, Vector2.zero, Vector2.zero);
            loading.SetActive(false);
            GameObject empty = AddText(root, "Empty", "No players yet", 40f, TextAlignmentOptions.Center, MutedText).gameObject;
            Stretch((RectTransform)empty.transform, Vector2.zero, Vector2.zero);
            empty.SetActive(false);
            GameObject error = AddText(root, "Error", "Can't load the leaderboard", 40f, TextAlignmentOptions.Center, MutedText).gameObject;
            Stretch((RectTransform)error.transform, Vector2.zero, Vector2.zero);
            error.SetActive(false);

            TMP_Text stale = AddText(root, "Stale", string.Empty, 28f, TextAlignmentOptions.Center, MutedText);
            Anchor(stale.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -40f), new Vector2(0f, 0f));
            stale.gameObject.SetActive(false);

            LeaderboardBoardView view = root.gameObject.AddComponent<LeaderboardBoardView>();
            view.Wire(list, template, self, loading, empty, error, stale);
            return view;
        }

        private static LeaderboardScrollJumpButtons CreateJumpButtons(Transform parent)
        {
            RectTransform holder = NewRect("JumpButtons", parent);
            Anchor(holder, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-120f, -200f), new Vector2(-20f, 200f));
            RectTransform buttons = NewRect("Buttons", holder);
            Stretch(buttons, Vector2.zero, Vector2.zero);
            CanvasGroup group = buttons.gameObject.AddComponent<CanvasGroup>();
            Button top = CreateRoundButton(buttons, "Top", "▲", 1f);
            Button self = CreateRoundButton(buttons, "Self", "●", 0.5f);
            Button bottom = CreateRoundButton(buttons, "Bottom", "▼", 0f);
            LeaderboardScrollJumpButtons jump = holder.gameObject.AddComponent<LeaderboardScrollJumpButtons>();
            jump.Wire(group, top, self, bottom);
            return jump;
        }

        private static Button CreateRoundButton(Transform parent, string name, string label, float anchorY)
        {
            RectTransform rect = NewRect(name, parent);
            Anchor(rect, new Vector2(0.5f, anchorY), new Vector2(0.5f, anchorY), new Vector2(-50f, -50f), new Vector2(50f, 50f));
            Image image = AddImage(rect.gameObject, new Color(0.25f, 0.3f, 0.45f, 0.95f));
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            TMP_Text text = AddText(rect, "Label", label, 40f, TextAlignmentOptions.Center, Color.white);
            Stretch(text.rectTransform, Vector2.zero, Vector2.zero);
            return button;
        }

        private static (Button, GameObject) CreateTabButton(Transform parent, string label, float fromX, float toX)
        {
            RectTransform rect = NewRect(label + "Tab", parent);
            Anchor(rect, new Vector2(fromX, 0f), new Vector2(toX, 1f), new Vector2(8f, 0f), new Vector2(-8f, 0f));
            Image image = AddImage(rect.gameObject, CellColor);
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;

            RectTransform selected = NewRect("Selected", rect);
            Anchor(selected, new Vector2(0f, 0f), new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 10f));
            Image bar = AddImage(selected.gameObject, AccentColor);
            bar.raycastTarget = false;
            selected.gameObject.SetActive(false);

            TMP_Text text = AddText(rect, "Label", label, 44f, TextAlignmentOptions.Center, Color.white);
            Stretch(text.rectTransform, Vector2.zero, Vector2.zero);
            return (button, selected.gameObject);
        }

        // ---- tiện ích dựng UI ----

        private static RectTransform NewRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.layer = parent != null ? parent.gameObject.layer : 5;
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static void Stretch(RectTransform rect, Vector2 offsetMin, Vector2 offsetMax) =>
            Anchor(rect, Vector2.zero, Vector2.one, offsetMin, offsetMax);

        private static void Anchor(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static Image AddImage(GameObject go, Color color)
        {
            Image image = go.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private static TMP_Text AddText(Transform parent, string name, string text, float size, TextAlignmentOptions alignment, Color color)
        {
            RectTransform rect = NewRect(name, parent);
            TextMeshProUGUI tmp = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (buildFont != null)
                tmp.font = buildFont;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.raycastTarget = false;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            return tmp;
        }
    }
}
