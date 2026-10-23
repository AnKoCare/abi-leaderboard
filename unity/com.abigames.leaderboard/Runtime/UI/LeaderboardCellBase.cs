using System;
using UnityEngine;

namespace AbiGames.Leaderboard.UI
{
    /// <summary>
    /// Một dòng bảng xếp hạng. Game kế thừa để vẽ theo phong cách riêng (avatar, khung,
    /// hiệu ứng tên...) — hoặc dùng luôn <see cref="DefaultLeaderboardCell"/>.
    ///
    /// Một instance được dùng lại cho nhiều dòng khi cuộn (danh sách ảo): <see cref="Bind"/>
    /// phải vẽ lại TOÀN BỘ trạng thái, không được giả định ô còn sạch.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public abstract class LeaderboardCellBase : MonoBehaviour
    {
        /// <summary>Người chơi bấm vào dòng (vd. mở trang profile).</summary>
        public event Action<LeaderboardEntry> Clicked;

        /// <summary>Dòng đang hiển thị (null khi ô chưa gắn dữ liệu).</summary>
        public LeaderboardEntry Entry { get; private set; }

        /// <summary>Dòng của chính người chơi đang đăng nhập.</summary>
        public bool IsSelf { get; private set; }

        /// <summary>Gắn dữ liệu rồi vẽ. Gọi từ danh sách ảo và từ ô "hạng của tôi".</summary>
        public void Bind(LeaderboardEntry entry, bool isSelf)
        {
            Entry = entry;
            IsSelf = isSelf;
            if (entry != null)
                Render(entry, isSelf);
        }

        /// <summary>Vẽ một dòng. <paramref name="entry"/> không null.</summary>
        protected abstract void Render(LeaderboardEntry entry, bool isSelf);

        /// <summary>Gọi từ nút / vùng bấm của ô.</summary>
        protected void NotifyClicked()
        {
            if (Entry != null)
                Clicked?.Invoke(Entry);
        }
    }
}
