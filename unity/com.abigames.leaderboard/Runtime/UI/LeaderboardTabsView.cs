using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AbiGames.Leaderboard.UI
{
    public enum LeaderboardTabKind
    {
        /// <summary>Board World (<c>ww</c>).</summary>
        World = 0,
        /// <summary>Board quốc gia của người chơi (<see cref="LeaderboardTabsView.PlayerCountryProvider"/>).</summary>
        PlayerCountry = 1,
        /// <summary>Board cố định (<see cref="LeaderboardTab.FixedBoard"/>).</summary>
        Fixed = 2,
    }

    [Serializable]
    public class LeaderboardTab
    {
        public Button Button;
        [Tooltip("Bật khi tab đang chọn.")]
        public GameObject SelectedIndicator;
        public LeaderboardBoardView View;
        public LeaderboardTabKind Kind;
        [Tooltip("Chỉ dùng khi Kind = Fixed.")]
        public string FixedBoard;
        [Tooltip("Hiện thay cho bảng khi tab bị khoá (IsTabLocked trả true).")]
        public GameObject LockedRoot;
    }

    /// <summary>
    /// Các tab World / Country (/ board cố định) — mỗi tab một <see cref="LeaderboardBoardView"/>.
    /// Chọn tab thì bật đúng bảng, đặt board cho nó rồi tải; tab bị khoá thì hiện
    /// <see cref="LeaderboardTab.LockedRoot"/> và không gọi mạng.
    /// </summary>
    public class LeaderboardTabsView : MonoBehaviour
    {
        [field: SerializeField] public List<LeaderboardTab> Tabs { get; private set; } = new List<LeaderboardTab>();
        [field: SerializeField] public int DefaultTab { get; private set; }
        [field: Tooltip("Board quốc gia khi người chơi chưa chọn quốc gia.")]
        [field: SerializeField] public string FallbackCountry { get; private set; } = "us";
        [field: SerializeField] public LeaderboardScrollJumpButtons JumpButtons { get; private set; }
        [field: Tooltip("Tự mở tab (đang chọn / mặc định) khi GameObject bật.")]
        [field: SerializeField] public bool OpenOnEnable { get; private set; } = true;

        /// <summary>Mã quốc gia của người chơi (vd. <c>() => PlayerData.Instance.CountryCode</c>).</summary>
        public Func<string> PlayerCountryProvider { get; set; }

        /// <summary>Tab có bị khoá không (vd. chưa đủ level). Null = không tab nào khoá.</summary>
        public Func<LeaderboardTab, bool> IsTabLocked { get; set; }

        public int SelectedIndex { get; private set; } = -1;
        public event Action<int> TabChanged;

        private bool listenersBound;

        protected virtual void Awake()
        {
            BindButtons();
        }

        protected virtual void OnEnable()
        {
            if (OpenOnEnable && Tabs.Count > 0)
                Select(SelectedIndex >= 0 ? SelectedIndex : DefaultTab);
        }

        private void BindButtons()
        {
            if (listenersBound)
                return;
            for (int i = 0; i < Tabs.Count; i++)
            {
                int index = i;
                if (Tabs[i].Button != null)
                    Tabs[i].Button.onClick.AddListener(() => Select(index));
            }
            listenersBound = true;
        }

        public LeaderboardTab Selected => SelectedIndex >= 0 && SelectedIndex < Tabs.Count ? Tabs[SelectedIndex] : null;

        public void Select(int index)
        {
            if (Tabs.Count == 0)
                return;
            index = Mathf.Clamp(index, 0, Tabs.Count - 1);
            SelectedIndex = index;
            LeaderboardTab selected = Tabs[index];
            bool locked = IsTabLocked != null && IsTabLocked(selected);

            for (int i = 0; i < Tabs.Count; i++)
            {
                LeaderboardTab tab = Tabs[i];
                bool isSelected = i == index;
                SetActive(tab.SelectedIndicator, isSelected);
                SetActive(tab.LockedRoot, isSelected && locked);
                if (!isSelected && tab.View != null)
                    SetActive(tab.View.gameObject, false);
            }

            if (selected.View != null)
            {
                if (locked)
                {
                    SetActive(selected.View.gameObject, false);
                }
                else
                {
                    // Đặt board TRƯỚC khi bật — OnEnable của view tải ngay board đang ghi.
                    selected.View.Board = ResolveBoard(selected);
                    bool wasActive = selected.View.gameObject.activeSelf;
                    SetActive(selected.View.gameObject, true);
                    if (wasActive || !selected.View.RefreshOnEnable)
                        selected.View.Refresh();
                }
            }

            if (JumpButtons != null)
                JumpButtons.SetTarget(locked ? null : selected.View);
            TabChanged?.Invoke(index);
        }

        /// <summary>Board mà tab sẽ tải.</summary>
        public string ResolveBoard(LeaderboardTab tab)
        {
            switch (tab.Kind)
            {
                case LeaderboardTabKind.PlayerCountry:
                    string player = LeaderboardBoards.Normalize(PlayerCountryProvider?.Invoke());
                    if (LeaderboardBoards.IsValid(player) && player != LeaderboardBoards.World)
                        return player;
                    string fallback = LeaderboardBoards.Normalize(FallbackCountry);
                    return LeaderboardBoards.IsValid(fallback) ? fallback : "us";
                case LeaderboardTabKind.Fixed:
                    string fixedBoard = LeaderboardBoards.Normalize(tab.FixedBoard);
                    return LeaderboardBoards.IsValid(fixedBoard) ? fixedBoard : LeaderboardBoards.World;
                default:
                    return LeaderboardBoards.World;
            }
        }

        private static void SetActive(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on)
                go.SetActive(on);
        }

        /// <summary>Thêm tab khi dựng bằng code (<see cref="LeaderboardPanelFactory"/>).</summary>
        internal void Wire(List<LeaderboardTab> tabs, LeaderboardScrollJumpButtons jumpButtons)
        {
            Tabs = tabs;
            JumpButtons = jumpButtons;
            if (Application.isPlaying && listenersBound)
            {
                listenersBound = false;
                BindButtons();
            }
        }
    }
}
