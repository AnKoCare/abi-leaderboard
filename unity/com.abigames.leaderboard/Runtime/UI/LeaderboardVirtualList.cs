using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AbiGames.Leaderboard.UI
{
    /// <summary>
    /// Danh sách ảo cho bảng xếp hạng: chỉ dựng đủ ô phủ viewport (+ vài ô đệm) rồi gắn lại dữ
    /// liệu khi cuộn, nên 1.000 dòng tốn đúng chi phí của khoảng 15 ô.
    ///
    /// Gắn lên <c>Content</c> của ScrollRect, THAY CHO VerticalLayoutGroup + ContentSizeFitter
    /// (hai component đó dựng lại cả list mỗi lần đổi dữ liệu — không dùng được ở 1.000 dòng).
    ///
    /// Ô mẫu (<see cref="Initialize"/>) KHÔNG được nằm trong Content: mọi con có sẵn của Content
    /// bị dọn lúc khởi tạo (ô designer để xem trước).
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class LeaderboardVirtualList : MonoBehaviour
    {
        [field: Header("Kích thước dòng")]
        [field: SerializeField] public float CellHeight { get; private set; } = 140f;
        [field: SerializeField] public float Spacing { get; private set; } = 8f;
        [field: SerializeField] public float PaddingTop { get; private set; } = 10f;
        [field: SerializeField] public float PaddingBottom { get; private set; } = 40f;
        [field: SerializeField] public float PaddingLeft { get; private set; } = 16f;
        [field: SerializeField] public float PaddingRight { get; private set; } = 16f;

        [field: Tooltip("Số dòng dựng thêm ngoài viewport mỗi phía để cuộn nhanh không thấy ô trống.")]
        [field: SerializeField] public int BufferCells { get; private set; } = 2;

        /// <summary>Người chơi bấm một dòng.</summary>
        public event Action<LeaderboardEntry> EntryClicked;

        private readonly List<LeaderboardEntry> items = new List<LeaderboardEntry>(1000);
        private readonly List<LeaderboardCellBase> cells = new List<LeaderboardCellBase>(16);
        private readonly List<int> slotItemIndex = new List<int>(16);

        private LeaderboardCellBase template;
        private string selfUid;
        private RectTransform content;
        private RectTransform viewport;
        private ScrollRect scrollRect;
        private bool layoutDirty;
        private int lastFirstIndex = -1;

        private float Pitch => CellHeight + Spacing;

        public int Count => items.Count;
        public IReadOnlyList<LeaderboardEntry> Items => items;
        /// <summary>Số ô đã dựng (cho test / đo hiệu năng).</summary>
        public int PooledCellCount => cells.Count;
        public ScrollRect ScrollRect => scrollRect;

        private void Awake()
        {
            content = (RectTransform)transform;
        }

        /// <summary>Gắn ô mẫu. Gọi một lần trước <see cref="SetItems"/>.</summary>
        public void Initialize(LeaderboardCellBase cellTemplate)
        {
            if (cellTemplate == null)
                throw new ArgumentNullException(nameof(cellTemplate));
            if (template == cellTemplate)
                return;

            if (content == null)
                content = (RectTransform)transform;
            scrollRect = GetComponentInParent<ScrollRect>();
            viewport = scrollRect != null && scrollRect.viewport != null
                ? scrollRect.viewport
                : content.parent as RectTransform;

            // Đổi ô mẫu: bỏ pool cũ.
            foreach (LeaderboardCellBase cell in cells)
            {
                if (cell != null)
                    Destroy(cell.gameObject);
            }
            cells.Clear();
            slotItemIndex.Clear();
            template = cellTemplate;
            RemoveDesignTimeChildren();
            layoutDirty = true;
        }

        private void RemoveDesignTimeChildren()
        {
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                Transform child = content.GetChild(i);
                if (template != null && child == template.transform)
                    continue;
                Destroy(child.gameObject);
            }
        }

        /// <summary>
        /// Thay dữ liệu. Danh sách mới chỉ NỐI THÊM vào danh sách cũ (trang đầu → đủ bảng, cùng
        /// các object) thì giữ nguyên các ô đang hiện, không vẽ lại.
        /// </summary>
        public void SetItems(IReadOnlyList<LeaderboardEntry> entries, string selfUid)
        {
            bool sameSelf = this.selfUid == selfUid;
            bool isAppend = sameSelf && entries != null && entries.Count >= items.Count;
            if (isAppend)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (!ReferenceEquals(items[i], entries[i]))
                    {
                        isAppend = false;
                        break;
                    }
                }
            }

            this.selfUid = selfUid;
            if (isAppend)
            {
                for (int i = items.Count; i < entries.Count; i++)
                {
                    if (entries[i] != null)
                        items.Add(entries[i]);
                }
            }
            else
            {
                items.Clear();
                if (entries != null)
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        if (entries[i] != null)
                            items.Add(entries[i]);
                    }
                }
                InvalidateBindings();
            }

            layoutDirty = true;
            ApplyLayout();
        }

        public void Clear() => SetItems(Array.Empty<LeaderboardEntry>(), selfUid);

        /// <summary>Tính lại vị trí + dữ liệu các ô ngay trong frame này.</summary>
        public void ForceRefresh()
        {
            layoutDirty = true;
            ApplyLayout();
        }

        public void ScrollToTop()
        {
            if (scrollRect != null)
                scrollRect.velocity = Vector2.zero;
            SetScrollY(0f);
        }

        /// <summary>Cuộn để dòng <paramref name="index"/> nằm giữa viewport (hoặc sát đỉnh nếu <paramref name="center"/> = false).</summary>
        public void ScrollToIndex(int index, bool center = true)
        {
            if (index < 0 || index >= items.Count || content == null)
                return;
            float viewportHeight = viewport != null ? viewport.rect.height : 0f;
            float rowTop = PaddingTop + index * Pitch;
            float target = center ? rowTop - (viewportHeight - CellHeight) * 0.5f : rowTop;
            float maxScroll = Mathf.Max(0f, ContentHeight() - viewportHeight);
            if (scrollRect != null)
                scrollRect.velocity = Vector2.zero;
            SetScrollY(Mathf.Clamp(target, 0f, maxScroll));
        }

        /// <summary>Vị trí của uid trong danh sách; -1 nếu không có.</summary>
        public int IndexOfUid(string uid)
        {
            if (string.IsNullOrEmpty(uid))
                return -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Uid == uid)
                    return i;
            }
            return -1;
        }

        private void SetScrollY(float y)
        {
            if (content == null)
                return;
            Vector2 pos = content.anchoredPosition;
            content.anchoredPosition = new Vector2(pos.x, y);
            layoutDirty = true;
            ApplyLayout();
        }

        private void LateUpdate()
        {
            ApplyLayout();
        }

        private void ApplyLayout()
        {
            if (content == null || template == null)
                return;

            float viewportHeight = viewport != null ? viewport.rect.height : 0f;
            if (viewportHeight <= 0f)
            {
                // Viewport chưa có kích thước (panel vừa bật) — thử lại ở frame sau.
                layoutDirty = true;
                return;
            }

            bool layoutChanged = layoutDirty;
            if (layoutChanged)
                UpdateContentSize();
            EnsurePool(viewportHeight);
            UpdateVisibleCells(layoutChanged);
            layoutDirty = false;
        }

        private float ContentHeight()
        {
            return items.Count > 0
                ? PaddingTop + items.Count * Pitch - Spacing + PaddingBottom
                : PaddingTop + PaddingBottom;
        }

        private void UpdateContentSize()
        {
            float height = ContentHeight();
            Vector2 size = content.sizeDelta;
            if (!Mathf.Approximately(size.y, height))
                content.sizeDelta = new Vector2(size.x, height);
        }

        private void EnsurePool(float viewportHeight)
        {
            int needed = Mathf.CeilToInt(viewportHeight / Pitch) + BufferCells * 2 + 1;
            needed = Mathf.Min(needed, Mathf.Max(1, items.Count));
            if (cells.Count >= needed)
                return;

            while (cells.Count < needed)
            {
                LeaderboardCellBase cell = Instantiate(template, content);
                cell.name = $"{template.name}_{cells.Count}";
                cell.gameObject.SetActive(false);
                cell.Clicked += OnCellClicked;
                cells.Add(cell);
                slotItemIndex.Add(-1);
            }
            // Pool đổi kích thước → ánh xạ slot theo modulo đổi theo, phải gắn lại toàn bộ.
            InvalidateBindings();
        }

        private void InvalidateBindings()
        {
            for (int i = 0; i < slotItemIndex.Count; i++)
                slotItemIndex[i] = -1;
            lastFirstIndex = -1;
        }

        private void UpdateVisibleCells(bool layoutChanged)
        {
            int poolSize = cells.Count;
            if (poolSize == 0)
                return;

            int count = items.Count;
            if (count == 0)
            {
                for (int slot = 0; slot < poolSize; slot++)
                {
                    SetActive(cells[slot], false);
                    slotItemIndex[slot] = -1;
                }
                lastFirstIndex = -1;
                return;
            }

            float scrollY = Mathf.Max(0f, content.anchoredPosition.y);
            int first = Mathf.FloorToInt((scrollY - PaddingTop) / Pitch) - BufferCells;
            first = Mathf.Clamp(first, 0, Mathf.Max(0, count - poolSize));
            if (!layoutChanged && first == lastFirstIndex)
                return;
            lastFirstIndex = first;

            for (int offset = 0; offset < poolSize; offset++)
            {
                int itemIndex = first + offset;
                int slot = itemIndex % poolSize;
                LeaderboardCellBase cell = cells[slot];

                if (itemIndex >= count)
                {
                    SetActive(cell, false);
                    slotItemIndex[slot] = -1;
                    continue;
                }

                bool needsRebind = slotItemIndex[slot] != itemIndex;
                // Layout đổi (viewport đổi kích thước, đổi padding) → đặt lại vị trí cả khi ô
                // vẫn giữ đúng dòng, không thì dòng đứng ở toạ độ cũ.
                if (needsRebind || layoutChanged)
                    PlaceRow((RectTransform)cell.transform, PaddingTop + itemIndex * Pitch);
                if (needsRebind)
                {
                    slotItemIndex[slot] = itemIndex;
                    LeaderboardEntry entry = items[itemIndex];
                    cell.Bind(entry, !string.IsNullOrEmpty(selfUid) && entry.Uid == selfUid);
                }
                SetActive(cell, true);
            }
        }

        /// <summary>Kéo ngang theo Content, mép trên cách đỉnh <paramref name="top"/>.</summary>
        private void PlaceRow(RectTransform row, float top)
        {
            row.anchorMin = new Vector2(0f, 1f);
            row.anchorMax = new Vector2(1f, 1f);
            row.pivot = new Vector2(0.5f, 1f);
            row.offsetMin = new Vector2(PaddingLeft, -(top + CellHeight));
            row.offsetMax = new Vector2(-PaddingRight, -top);
            row.localScale = Vector3.one;
        }

        private static void SetActive(Component cell, bool on)
        {
            if (cell.gameObject.activeSelf != on)
                cell.gameObject.SetActive(on);
        }

        private void OnCellClicked(LeaderboardEntry entry) => EntryClicked?.Invoke(entry);

        /// <summary>Đặt kích thước dòng khi dựng bằng code.</summary>
        internal void Configure(float cellHeight, float spacing, float paddingTop, float paddingBottom, float paddingSide)
        {
            CellHeight = cellHeight;
            Spacing = spacing;
            PaddingTop = paddingTop;
            PaddingBottom = paddingBottom;
            PaddingLeft = paddingSide;
            PaddingRight = paddingSide;
            layoutDirty = true;
        }
    }
}
