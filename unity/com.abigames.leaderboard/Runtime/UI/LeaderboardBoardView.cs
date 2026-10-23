using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace AbiGames.Leaderboard.UI
{
    /// <summary>
    /// Một bảng xếp hạng trên màn hình: danh sách ảo + ô "hạng của tôi" ghim ngoài danh sách +
    /// các trạng thái đang tải / trống / lỗi / "dữ liệu lúc ...".
    ///
    /// Lấy dữ liệu qua <see cref="LeaderboardBoardLoader"/>: mặc định
    /// <see cref="LeaderboardRuntime.Loader"/>, hoặc loader truyền vào <see cref="Bind"/>.
    /// Tắt GameObject (đổi tab, đóng popup) thì lượt tải đang chạy bị huỷ.
    /// </summary>
    public class LeaderboardBoardView : MonoBehaviour
    {
        [field: SerializeField] public LeaderboardVirtualList List { get; private set; }
        [field: Tooltip("Ô mẫu của danh sách — đặt NGOÀI Content của ScrollRect, để tắt.")]
        [field: SerializeField] public LeaderboardCellBase CellTemplate { get; private set; }
        [field: Tooltip("Ô 'hạng của tôi' ghim ngoài danh sách. Để trống nếu không cần.")]
        [field: SerializeField] public LeaderboardCellBase SelfCell { get; private set; }
        [field: SerializeField] public GameObject LoadingRoot { get; private set; }
        [field: SerializeField] public GameObject EmptyRoot { get; private set; }
        [field: SerializeField] public GameObject ErrorRoot { get; private set; }
        [field: Tooltip("Hiện khi đang xem dữ liệu cache (mất mạng / server lỗi).")]
        [field: SerializeField] public TMP_Text StaleLabel { get; private set; }
        [field: SerializeField] public string StaleFormat { get; private set; } = "Data from {0:HH:mm dd/MM}";
        [field: Tooltip("ww = World, mã ISO viết thường = quốc gia. LeaderboardTabsView tự đặt khi dùng tab.")]
        [field: SerializeField] public string Board { get; set; } = LeaderboardBoards.World;
        [field: Tooltip("Tự tải khi GameObject bật.")]
        [field: SerializeField] public bool RefreshOnEnable { get; private set; } = true;

        /// <summary>
        /// Profile của chính người chơi (cùng dạng <c>info</c> server trả) — vẽ ô "hạng của tôi"
        /// khi người chơi nằm ngoài top, lúc đó server chỉ trả hạng, không trả profile.
        /// </summary>
        public Func<JObject> SelfProfileProvider { get; set; }

        /// <summary>Người chơi bấm một dòng (kể cả ô "hạng của tôi").</summary>
        public event Action<LeaderboardEntry> EntryClicked;
        /// <summary>Mỗi lần có dữ liệu mới (cache → trang đầu → đủ bảng).</summary>
        public event Action<LeaderboardSnapshot> SnapshotChanged;

        /// <summary>Snapshot đang hiển thị.</summary>
        public LeaderboardSnapshot Current { get; private set; }

        private LeaderboardBoardLoader boundLoader;
        private CancellationTokenSource loadCts;
        private bool listReady;

        private LeaderboardBoardLoader Loader => boundLoader ?? LeaderboardRuntime.Loader;

        /// <summary>Dùng loader riêng thay cho <see cref="LeaderboardRuntime.Loader"/> (game có DI riêng).</summary>
        public void Bind(LeaderboardBoardLoader loader) => boundLoader = loader;

        protected virtual void OnEnable()
        {
            if (RefreshOnEnable && Loader != null)
                Refresh();
        }

        protected virtual void OnDisable()
        {
            CancelLoad();
            SetActive(LoadingRoot, false);
        }

        /// <summary>Tải (hoặc dùng lại dữ liệu vừa tải) board <see cref="Board"/>.</summary>
        public void Refresh(bool force = false)
        {
            LeaderboardBoardLoader loader = Loader;
            if (loader == null)
            {
                Debug.LogError($"[{name}] Chưa có loader: gọi LeaderboardRuntime.Initialize(...) hoặc Bind(loader) trước.");
                return;
            }
            if (!EnsureList())
                return;
            CancelLoad();

            string board = LeaderboardBoards.Normalize(Board);
            if (Current == null || Current.Board != board)
            {
                // Đổi board: dọn bảng cũ để không hiện nhầm dữ liệu của board khác trong lúc chờ.
                Current = null;
                List.Clear();
                SetActive(SelfCell != null ? SelfCell.gameObject : null, false);
                SetActive(EmptyRoot, false);
                SetActive(ErrorRoot, false);
                SetActive(StaleLabel != null ? StaleLabel.gameObject : null, false);
                SetActive(LoadingRoot, true);
            }

            loadCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
            LoadAsync(loader, board, force, loadCts.Token).Forget();
        }

        /// <summary>Cuộn tới dòng của chính người chơi nếu có trong bảng.</summary>
        public bool ScrollToSelf()
        {
            int index = List != null ? List.IndexOfUid(SelfUid()) : -1;
            if (index < 0)
                return false;
            List.ScrollToIndex(index);
            return true;
        }

        private async UniTaskVoid LoadAsync(LeaderboardBoardLoader loader, string board, bool force, CancellationToken token)
        {
            try
            {
                await loader.LoadAsync(board, Render, force, token);
            }
            catch (OperationCanceledException)
            {
                // Đổi tab / đóng bảng giữa chừng.
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                SetActive(LoadingRoot, false);
                if (Current == null || Current.Entries.Count == 0)
                    SetActive(ErrorRoot, true);
            }
        }

        private void Render(LeaderboardSnapshot snapshot)
        {
            // Snapshot của board cũ tới muộn (đã đổi tab) — bỏ.
            if (snapshot.Board != LeaderboardBoards.Normalize(Board))
                return;

            Current = snapshot;
            string selfUid = SelfUid();
            List.SetItems(snapshot.Entries, selfUid);

            bool hasRows = snapshot.Entries.Count > 0;
            bool finished = snapshot.Stage == LeaderboardLoadStage.Complete || snapshot.Stage == LeaderboardLoadStage.Failed;
            SetActive(LoadingRoot, !hasRows && !finished);
            SetActive(EmptyRoot, !hasRows && snapshot.Stage == LeaderboardLoadStage.Complete);
            SetActive(ErrorRoot, !hasRows && snapshot.Stage == LeaderboardLoadStage.Failed);

            if (StaleLabel != null)
            {
                bool stale = snapshot.IsFromCache && snapshot.CachedAtLocalTime.HasValue;
                StaleLabel.gameObject.SetActive(stale);
                if (stale)
                    StaleLabel.text = string.Format(StaleFormat, snapshot.CachedAtLocalTime.Value);
            }

            RenderSelf(snapshot, selfUid);
            SnapshotChanged?.Invoke(snapshot);
        }

        private void RenderSelf(LeaderboardSnapshot snapshot, string selfUid)
        {
            if (SelfCell == null)
                return;
            LeaderboardRank rank = snapshot.SelfRank;
            if (rank == null)
            {
                SelfCell.gameObject.SetActive(false);
                return;
            }

            // Có trong bảng thì dùng luôn dòng đó (đủ profile từ server).
            LeaderboardEntry entry = null;
            int index = rank.Rank - 1;
            if (index >= 0 && index < snapshot.Entries.Count && snapshot.Entries[index].Uid == rank.Uid)
                entry = snapshot.Entries[index];
            if (entry == null)
            {
                entry = new LeaderboardEntry
                {
                    Rank = rank.Rank,
                    Uid = rank.Uid ?? selfUid,
                    Score = rank.Score,
                    SecondaryScore = rank.SecondaryScore,
                    CountryCode = rank.CountryCode,
                    Info = SelfProfileProvider?.Invoke(),
                };
            }
            SelfCell.gameObject.SetActive(true);
            SelfCell.Bind(entry, true);
        }

        private bool EnsureList()
        {
            if (listReady)
                return true;
            if (List == null || CellTemplate == null)
            {
                Debug.LogError($"[{name}] Thiếu List hoặc CellTemplate.");
                return false;
            }
            CellTemplate.gameObject.SetActive(false);
            List.Initialize(CellTemplate);
            List.EntryClicked += OnEntryClicked;
            if (SelfCell != null)
                SelfCell.Clicked += OnEntryClicked;
            listReady = true;
            return true;
        }

        private void OnEntryClicked(LeaderboardEntry entry) => EntryClicked?.Invoke(entry);

        private string SelfUid()
        {
            try
            {
                return Loader?.Config.SelfUidProvider?.Invoke();
            }
            catch
            {
                return null;
            }
        }

        private void CancelLoad()
        {
            if (loadCts == null)
                return;
            loadCts.Cancel();
            loadCts.Dispose();
            loadCts = null;
        }

        private static void SetActive(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on)
                go.SetActive(on);
        }

        /// <summary>Gắn tham chiếu khi dựng bằng code (<see cref="LeaderboardPanelFactory"/>).</summary>
        internal void Wire(LeaderboardVirtualList list, LeaderboardCellBase template, LeaderboardCellBase selfCell,
            GameObject loading, GameObject empty, GameObject error, TMP_Text stale)
        {
            List = list;
            CellTemplate = template;
            SelfCell = selfCell;
            LoadingRoot = loading;
            EmptyRoot = empty;
            ErrorRoot = error;
            StaleLabel = stale;
        }
    }
}
