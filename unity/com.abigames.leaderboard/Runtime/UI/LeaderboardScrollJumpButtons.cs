using UnityEngine;
using UnityEngine.UI;

namespace AbiGames.Leaderboard.UI
{
    /// <summary>
    /// Nút "lên đầu / tới hạng của tôi / xuống cuối" cho bảng đang mở.
    ///  - Mặc định ẩn; người chơi bắt đầu lướt thì hiện.
    ///  - Ngừng lướt <see cref="IdleBeforeFadeSeconds"/> giây thì mờ dần rồi ẩn.
    ///
    /// Nhận biết "đang lướt" bằng độ dịch của Content, không dùng <c>ScrollRect.onValueChanged</c>:
    /// sự kiện đó cũng bắn khi list đổi chiều cao (lúc tải xong dữ liệu) và làm nút tự hiện dù
    /// người chơi chưa chạm.
    /// </summary>
    public class LeaderboardScrollJumpButtons : MonoBehaviour
    {
        [field: Tooltip("CanvasGroup bọc cả cụm nút — dùng để mờ dần.")]
        [field: SerializeField] public CanvasGroup Group { get; private set; }
        [field: SerializeField] public Button TopButton { get; private set; }
        [field: SerializeField] public Button SelfButton { get; private set; }
        [field: SerializeField] public Button BottomButton { get; private set; }
        [field: SerializeField] public float IdleBeforeFadeSeconds { get; private set; } = 2f;
        [field: SerializeField] public float FadeDuration { get; private set; } = 0.35f;
        [field: Tooltip("Thời gian trượt tới nơi. 0 = nhảy tức thì.")]
        [field: SerializeField] public float JumpDuration { get; private set; } = 0.2f;
        [field: SerializeField] public float ScrollEpsilon { get; private set; } = 0.5f;

        private LeaderboardBoardView target;
        private ScrollRect scrollRect;
        private RectTransform content;
        private float lastContentY;
        private float idleTimer;
        private bool visible;
        private bool jumping;
        private float jumpFrom;
        private float jumpTo;
        private float jumpElapsed;

        private void Awake()
        {
            if (TopButton != null)
                TopButton.onClick.AddListener(JumpToTop);
            if (BottomButton != null)
                BottomButton.onClick.AddListener(JumpToBottom);
            if (SelfButton != null)
                SelfButton.onClick.AddListener(JumpToSelf);
            HideImmediate();
        }

        private void OnDestroy()
        {
            if (TopButton != null)
                TopButton.onClick.RemoveListener(JumpToTop);
            if (BottomButton != null)
                BottomButton.onClick.RemoveListener(JumpToBottom);
            if (SelfButton != null)
                SelfButton.onClick.RemoveListener(JumpToSelf);
        }

        private void OnDisable()
        {
            jumping = false;
            HideImmediate();
        }

        /// <summary>Trỏ vào bảng đang mở (null = không bảng nào). <see cref="LeaderboardTabsView"/> tự gọi khi đổi tab.</summary>
        public void SetTarget(LeaderboardBoardView view)
        {
            target = view;
            scrollRect = view != null && view.List != null ? view.List.GetComponentInParent<ScrollRect>() : null;
            content = scrollRect != null ? scrollRect.content : null;
            lastContentY = content != null ? content.anchoredPosition.y : 0f;
            jumping = false;
            HideImmediate();
        }

        public void JumpToTop() => StartJump(1f);

        public void JumpToBottom() => StartJump(0f);

        public void JumpToSelf()
        {
            if (target == null)
                return;
            jumping = false;
            if (target.ScrollToSelf())
                NotifyActivity();
        }

        private void Update()
        {
            if (scrollRect == null || content == null)
                return;
            UpdateJump();

            float y = content.anchoredPosition.y;
            if (Mathf.Abs(y - lastContentY) > ScrollEpsilon)
            {
                lastContentY = y;
                NotifyActivity();
            }

            if (!visible)
                return;
            idleTimer += Time.unscaledDeltaTime;
            if (idleTimer < IdleBeforeFadeSeconds)
                return;
            float alpha = FadeDuration <= 0f ? 0f : (Group != null ? Group.alpha : 0f) - Time.unscaledDeltaTime / FadeDuration;
            if (alpha <= 0f)
                HideImmediate();
            else
                SetAlpha(alpha);
        }

        private void NotifyActivity()
        {
            if (!IsScrollable())
                return;
            idleTimer = 0f;
            visible = true;
            SetButtonsActive(true);
            SetAlpha(1f);
        }

        private void StartJump(float normalizedTarget)
        {
            if (scrollRect == null || !IsScrollable())
                return;
            NotifyActivity();
            jumpFrom = Mathf.Clamp01(scrollRect.verticalNormalizedPosition);
            jumpTo = normalizedTarget;
            jumpElapsed = 0f;
            jumping = JumpDuration > 0f;
            if (!jumping)
                ApplyNormalized(jumpTo);
        }

        private void UpdateJump()
        {
            if (!jumping)
                return;
            jumpElapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(jumpElapsed / JumpDuration);
            ApplyNormalized(Mathf.Lerp(jumpFrom, jumpTo, Mathf.SmoothStep(0f, 1f, t)));
            if (t >= 1f)
                jumping = false;
        }

        /// <summary>ScrollRect dùng 1 = đỉnh, 0 = đáy — không phụ thuộc pivot/anchor của Content.</summary>
        private void ApplyNormalized(float normalized)
        {
            scrollRect.velocity = Vector2.zero;
            scrollRect.verticalNormalizedPosition = normalized;
        }

        /// <summary>Content ngắn hơn viewport thì không có gì để lướt.</summary>
        private bool IsScrollable()
        {
            RectTransform viewport = scrollRect.viewport != null ? scrollRect.viewport : (RectTransform)scrollRect.transform;
            return content.rect.height > viewport.rect.height + 1f;
        }

        private void SetButtonsActive(bool on)
        {
            if (Group != null && Group.gameObject != gameObject && Group.gameObject.activeSelf != on)
                Group.gameObject.SetActive(on);
        }

        private void SetAlpha(float alpha)
        {
            if (Group == null)
                return;
            Group.alpha = Mathf.Clamp01(alpha);
            bool interactable = Group.alpha > 0f;
            Group.interactable = interactable;
            Group.blocksRaycasts = interactable;
        }

        private void HideImmediate()
        {
            visible = false;
            idleTimer = 0f;
            SetAlpha(0f);
            SetButtonsActive(false);
        }

        /// <summary>Gắn tham chiếu khi dựng bằng code (<see cref="LeaderboardPanelFactory"/>).</summary>
        internal void Wire(CanvasGroup group, Button top, Button self, Button bottom)
        {
            Group = group;
            TopButton = top;
            SelfButton = self;
            BottomButton = bottom;
        }
    }
}
