using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AbiGames.Leaderboard.UI
{
    /// <summary>
    /// Ô mặc định: hạng, avatar, tên, cờ / mã quốc gia, điểm, điểm phụ. Đủ để chạy ngay; game
    /// muốn đẹp hơn thì kế thừa <see cref="LeaderboardCellBase"/> (hoặc lớp này) và vẽ lại.
    ///
    /// Tên field trong <c>info</c> đổi được trong Inspector — khớp với option <c>profile</c> của server.
    /// </summary>
    public class DefaultLeaderboardCell : LeaderboardCellBase
    {
        [field: Header("Thành phần")]
        [field: SerializeField] public TMP_Text RankText { get; private set; }
        [field: SerializeField] public TMP_Text NameText { get; private set; }
        [field: SerializeField] public TMP_Text ScoreText { get; private set; }
        [field: Tooltip("Ẩn khi điểm phụ = 0.")]
        [field: SerializeField] public TMP_Text SecondaryText { get; private set; }
        [field: SerializeField] public Image AvatarImage { get; private set; }
        [field: SerializeField] public Image FlagImage { get; private set; }
        [field: Tooltip("Hiện mã quốc gia (VN, US...) khi không có cờ.")]
        [field: SerializeField] public TMP_Text CountryText { get; private set; }
        [field: SerializeField] public GameObject SelfHighlight { get; private set; }
        [field: SerializeField] public Button Button { get; private set; }

        [field: Header("Dữ liệu")]
        [field: SerializeField] public LeaderboardAvatarSet AvatarSet { get; private set; }
        [field: SerializeField] public LeaderboardCountryFlagSet FlagSet { get; private set; }
        [field: SerializeField] public string NameKey { get; private set; } = "PlayerName";
        [field: SerializeField] public string AvatarKey { get; private set; } = "AvatarIndex";
        [field: Tooltip("Màu chữ hạng 1, 2, 3. Các hạng khác giữ màu gốc của RankText.")]
        [field: SerializeField] public Color[] TopRankColors { get; private set; } =
        {
            new Color(1f, 0.78f, 0.15f),
            new Color(0.78f, 0.82f, 0.88f),
            new Color(0.86f, 0.52f, 0.28f),
        };
        [field: Tooltip("Màu avatar khi chưa có AvatarSet — mỗi chỉ số một màu, đủ để phân biệt khi dựng thử.")]
        [field: SerializeField] public Color[] PlaceholderAvatarColors { get; private set; } =
        {
            new Color(0.95f, 0.45f, 0.4f),
            new Color(0.4f, 0.7f, 0.95f),
            new Color(0.5f, 0.85f, 0.5f),
            new Color(0.95f, 0.8f, 0.35f),
            new Color(0.7f, 0.55f, 0.95f),
        };

        private Color rankBaseColor;
        private bool rankBaseColorCaptured;

        protected virtual void Awake()
        {
            if (Button == null)
                return;
            // Gỡ trước rồi mới gắn: dựng bằng code thì Wire() có thể đã gắn trước khi Awake chạy.
            Button.onClick.RemoveListener(NotifyClicked);
            Button.onClick.AddListener(NotifyClicked);
        }

        protected virtual void OnDestroy()
        {
            if (Button != null)
                Button.onClick.RemoveListener(NotifyClicked);
        }

        protected override void Render(LeaderboardEntry entry, bool isSelf)
        {
            if (RankText != null)
            {
                if (!rankBaseColorCaptured)
                {
                    rankBaseColor = RankText.color;
                    rankBaseColorCaptured = true;
                }
                RankText.SetText("{0}", entry.Rank);
                bool top = entry.Rank >= 1 && TopRankColors != null && entry.Rank <= TopRankColors.Length;
                RankText.color = top ? TopRankColors[entry.Rank - 1] : rankBaseColor;
            }

            if (NameText != null)
                NameText.text = entry.GetInfoString(NameKey, entry.Uid);
            if (ScoreText != null)
                ScoreText.SetText("{0}", entry.Score);
            if (SecondaryText != null)
            {
                bool hasSecondary = entry.SecondaryScore > 0;
                SecondaryText.gameObject.SetActive(hasSecondary);
                if (hasSecondary)
                    SecondaryText.SetText("{0}", entry.SecondaryScore);
            }

            RenderAvatar(entry.GetInfoInt(AvatarKey, 0));
            RenderCountry(entry.CountryCode);

            if (SelfHighlight != null)
                SelfHighlight.SetActive(isSelf);
        }

        private void RenderAvatar(int index)
        {
            if (AvatarImage == null)
                return;
            Sprite sprite = AvatarSet != null ? AvatarSet.Get(index) : null;
            AvatarImage.sprite = sprite;
            if (sprite != null)
                AvatarImage.color = Color.white;
            else if (PlaceholderAvatarColors != null && PlaceholderAvatarColors.Length > 0)
                AvatarImage.color = PlaceholderAvatarColors[Mathf.Abs(index) % PlaceholderAvatarColors.Length];
        }

        private void RenderCountry(string countryCode)
        {
            Sprite flag = FlagSet != null ? FlagSet.Get(countryCode) : null;
            if (FlagImage != null)
            {
                FlagImage.gameObject.SetActive(flag != null);
                FlagImage.sprite = flag;
            }
            if (CountryText != null)
            {
                bool showCode = flag == null && !string.IsNullOrEmpty(countryCode);
                CountryText.gameObject.SetActive(showCode);
                if (showCode)
                    CountryText.text = countryCode.ToUpperInvariant();
            }
        }

        /// <summary>Gắn tham chiếu khi dựng bằng code (<see cref="LeaderboardPanelFactory"/>).</summary>
        internal void Wire(TMP_Text rank, TMP_Text playerName, TMP_Text score, TMP_Text secondary,
            Image avatar, TMP_Text country, GameObject highlight, Button button)
        {
            RankText = rank;
            NameText = playerName;
            ScoreText = score;
            SecondaryText = secondary;
            AvatarImage = avatar;
            CountryText = country;
            SelfHighlight = highlight;
            Button = button;
            if (Button != null && Application.isPlaying)
            {
                Button.onClick.RemoveListener(NotifyClicked);
                Button.onClick.AddListener(NotifyClicked);
            }
        }
    }
}
