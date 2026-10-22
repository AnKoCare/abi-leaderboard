using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace AbiGames.Leaderboard
{
    /// <summary>Ảnh chụp một board để hiện lại ngay lần mở sau hoặc khi mất mạng.</summary>
    [Serializable]
    public class LeaderboardCacheSnapshot
    {
        public const int CurrentSchemaVersion = 1;

        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        [JsonProperty("board")] public string Board { get; set; }
        [JsonProperty("savedAt")] public long SavedAtUnixSeconds { get; set; }
        [JsonProperty("boardTotal")] public int BoardTotal { get; set; }
        [JsonProperty("entries")] public List<LeaderboardEntry> Entries { get; set; } = new List<LeaderboardEntry>();
        /// <summary>Hạng của người chơi lúc lưu — ô "hạng của tôi" không trống khi offline.</summary>
        [JsonProperty("selfRank")] public LeaderboardRank SelfRank { get; set; }
        /// <summary>uid lúc lưu: đổi tài khoản thì <see cref="SelfRank"/> cũ không còn là của mình.</summary>
        [JsonProperty("selfUid")] public string SelfUid { get; set; }

        [JsonIgnore]
        public DateTime SavedAtLocalTime => DateTimeOffset.FromUnixTimeSeconds(SavedAtUnixSeconds).ToLocalTime().DateTime;
    }

    public interface ILeaderboardCache
    {
        /// <summary>Null nếu chưa có / hỏng / khác phiên bản định dạng.</summary>
        UniTask<LeaderboardCacheSnapshot> LoadAsync(string board, CancellationToken cancellationToken);
        UniTask SaveAsync(LeaderboardCacheSnapshot snapshot, CancellationToken cancellationToken);
        /// <summary>Xoá mọi board — gọi khi thông tin hiển thị của người chơi đổi (đổi tên, đổi tài khoản).</summary>
        void ClearAll();
    }

    /// <summary>
    /// Mỗi board một file JSON trong <c>persistentDataPath/&lt;CacheFolder&gt;/&lt;StorageKey&gt;/</c>.
    ///
    /// Không dùng file save chính của game: 1.000 dòng ~250 KB mỗi board, nhét vào save là
    /// mọi lần save game nặng thêm. Serialize + I/O chạy trên thread pool nên không tốn frame
    /// nào của main thread; ghi ra file tạm rồi mới thay để tắt app giữa chừng không để lại
    /// file hỏng.
    /// </summary>
    public sealed class LeaderboardFileCache : ILeaderboardCache
    {
        private readonly string root;

        /// <param name="rootDirectory">Thư mục chứa file cache (tạo khi cần).</param>
        public LeaderboardFileCache(string rootDirectory)
        {
            root = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        }

        /// <summary>
        /// Tạo theo cấu hình; null nếu <see cref="LeaderboardConfig.CacheFolder"/> rỗng.
        /// Gọi trên main thread (đọc <c>Application.persistentDataPath</c>).
        /// </summary>
        public static LeaderboardFileCache FromConfig(LeaderboardConfig config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.CacheFolder))
                return null;
            return new LeaderboardFileCache(Path.Combine(
                Application.persistentDataPath,
                SafeName(config.CacheFolder),
                SafeName(string.IsNullOrWhiteSpace(config.StorageKey) ? "default" : config.StorageKey)));
        }

        public async UniTask<LeaderboardCacheSnapshot> LoadAsync(string board, CancellationToken cancellationToken)
        {
            string path = PathOf(board);
            LeaderboardCacheSnapshot snapshot = null;
#if !(UNITY_WEBGL && !UNITY_EDITOR)
            await UniTask.SwitchToThreadPool();
#endif
            try
            {
                if (File.Exists(path))
                    snapshot = JsonConvert.DeserializeObject<LeaderboardCacheSnapshot>(File.ReadAllText(path), LeaderboardJson.Settings);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LeaderboardCache] đọc cache '{board}' lỗi: {e.Message}");
                snapshot = null;
            }
            finally
            {
#if !(UNITY_WEBGL && !UNITY_EDITOR)
                await UniTask.SwitchToMainThread();
#endif
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (snapshot == null || snapshot.SchemaVersion != LeaderboardCacheSnapshot.CurrentSchemaVersion || snapshot.Entries == null)
                return null;
            return snapshot;
        }

        public async UniTask SaveAsync(LeaderboardCacheSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (snapshot == null || string.IsNullOrEmpty(snapshot.Board))
                return;
            string path = PathOf(snapshot.Board);
#if !(UNITY_WEBGL && !UNITY_EDITOR)
            await UniTask.SwitchToThreadPool();
#endif
            try
            {
                string json = JsonConvert.SerializeObject(snapshot, LeaderboardJson.Settings);
                Directory.CreateDirectory(root);
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LeaderboardCache] ghi cache '{snapshot.Board}' lỗi: {e.Message}");
            }
            finally
            {
#if !(UNITY_WEBGL && !UNITY_EDITOR)
                await UniTask.SwitchToMainThread();
#endif
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        public void ClearAll()
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LeaderboardCache] xoá cache lỗi: {e.Message}");
            }
        }

        private string PathOf(string board) => Path.Combine(root, SafeName(LeaderboardBoards.Normalize(board) ?? "unknown") + ".json");

        private static string SafeName(string name)
        {
            string safe = name.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                safe = safe.Replace(invalid, '_');
            return safe;
        }
    }
}
