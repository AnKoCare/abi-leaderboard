using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace AbiGames.Leaderboard
{
    public sealed class LeaderboardSubmitterOptions
    {
        /// <summary>Số lần thử tối đa cho một đợt gửi khi gặp lỗi tạm thời (mạng, 5xx, 429).</summary>
        public int MaxAttempts = 5;
        /// <summary>Chờ trước lần thử lại đầu; mỗi lần sau gấp đôi.</summary>
        public float FirstRetryDelaySeconds = 2f;
        public float MaxRetryDelaySeconds = 60f;
        /// <summary>Lưu điểm chưa gửi được vào PlayerPrefs — tắt game giữa chừng thì lần mở sau gửi tiếp.</summary>
        public bool PersistPending = true;
    }

    /// <summary>
    /// Gửi điểm kiểu "bắn rồi quên": gọi <see cref="Submit"/> mỗi khi thắng level, không cần chờ.
    ///  - Gộp: gọi dồn dập chỉ gửi số LỚN NHẤT, mỗi lúc tối đa một request.
    ///  - Bỏ qua lần gửi không cao hơn số server đã nhận trong phiên (server cũng chỉ nhận điểm tăng).
    ///  - Lỗi tạm thời: thử lại có giãn cách; hết lượt thì giữ điểm, lần <see cref="Submit"/> /
    ///    <see cref="ResendPending"/> sau gửi tiếp.
    ///  - Lỗi vĩnh viễn (403 bị cấm, 404 không có player, 400): bỏ điểm đó — gửi lại cũng hỏng y vậy.
    ///  - 401: giữ điểm; gọi <see cref="ResendPending"/> sau khi đăng nhập xong.
    ///
    /// Dùng trên main thread.
    /// </summary>
    public sealed class LeaderboardScoreSubmitter : IDisposable
    {
        private const string PrefsKeyPrefix = "abi_lb.pending.";

        private readonly LeaderboardClient client;
        private readonly LeaderboardSubmitterOptions options;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private readonly string prefsKey;

        private int? pendingScore;
        private int pendingSecondary;
        private int sentScore = -1;
        private int sentSecondary = -1;
        private string loadedForUid;
        private bool disposed;

        /// <summary>Server vừa nhận một điểm.</summary>
        public event Action<LeaderboardSubmitResult> Submitted;
        /// <summary>Ngừng gửi: lỗi vĩnh viễn, chưa đăng nhập, hoặc hết lượt thử.</summary>
        public event Action<LeaderboardError> Failed;

        public bool IsSending { get; private set; }
        public bool HasPending => pendingScore.HasValue;
        /// <summary>Điểm cao nhất server đã nhận trong phiên này; -1 nếu chưa gửi lần nào.</summary>
        public int HighestSentScore => sentScore;

        public LeaderboardScoreSubmitter(LeaderboardClient client, LeaderboardSubmitterOptions options = null)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.options = options ?? new LeaderboardSubmitterOptions();
            string storage = string.IsNullOrWhiteSpace(client.Config.StorageKey) ? "default" : client.Config.StorageKey;
            prefsKey = PrefsKeyPrefix + storage;
        }

        /// <summary>Ghi nhận điểm và gửi nền. Gọi bao nhiêu lần cũng được.</summary>
        public void Submit(int score, int secondaryScore = 0)
        {
            if (disposed || score < 0)
                return;
            LoadPersistedPending();
            if (score <= sentScore && secondaryScore <= sentSecondary)
                return;

            pendingScore = Math.Max(pendingScore ?? -1, score);
            pendingSecondary = Math.Max(pendingSecondary, Math.Max(0, secondaryScore));
            SavePending();
            Kick();
        }

        /// <summary>Gửi lại điểm còn chờ — gọi sau khi đăng nhập, khi app quay lại foreground, khi có mạng.</summary>
        public void ResendPending()
        {
            if (disposed)
                return;
            LoadPersistedPending();
            Kick();
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            cts.Cancel();
            cts.Dispose();
        }

        private void Kick()
        {
            if (!IsSending && pendingScore.HasValue)
                RunAsync().Forget();
        }

        private async UniTaskVoid RunAsync()
        {
            IsSending = true;
            try
            {
                int attempt = 0;
                while (!disposed && pendingScore.HasValue)
                {
                    int score = pendingScore.Value;
                    int secondary = pendingSecondary;

                    LeaderboardResult<LeaderboardSubmitResult> result;
                    try
                    {
                        result = await client.SubmitScoreAsync(score, secondary, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (result.IsSuccess)
                    {
                        attempt = 0;
                        sentScore = Math.Max(sentScore, score);
                        sentSecondary = Math.Max(sentSecondary, secondary);
                        // Trong lúc request bay có thể đã có điểm cao hơn được Submit — giữ lại, vòng sau gửi tiếp.
                        if (pendingScore.Value <= sentScore && pendingSecondary <= sentSecondary)
                            ClearPending();
                        Submitted?.Invoke(result.Value);
                        continue;
                    }

                    LeaderboardError error = result.Error;
                    if (!error.IsRetryable)
                    {
                        if (error.Kind != LeaderboardErrorKind.Unauthorized)
                            ClearPending();
                        Failed?.Invoke(error);
                        return;
                    }

                    attempt += 1;
                    if (attempt >= Math.Max(1, options.MaxAttempts))
                    {
                        Failed?.Invoke(error);
                        return;
                    }

                    float delay = Mathf.Min(
                        options.MaxRetryDelaySeconds,
                        options.FirstRetryDelaySeconds * Mathf.Pow(2f, attempt - 1));
                    try
                    {
                        await UniTask.Delay(TimeSpan.FromSeconds(Mathf.Max(0f, delay)), DelayType.Realtime, PlayerLoopTiming.Update, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
            finally
            {
                IsSending = false;
            }
        }

        // ---- lưu điểm chưa gửi ----
        // Lưu kèm uid: đổi tài khoản thì điểm chờ của tài khoản cũ không bị gửi nhầm cho tài khoản mới.

        private void LoadPersistedPending()
        {
            if (!options.PersistPending)
                return;
            string uid = CurrentUid();
            if (string.IsNullOrEmpty(uid) || uid == loadedForUid)
                return;

            if (loadedForUid != null)
            {
                // Đổi tài khoản giữa phiên: mọi thứ của tài khoản cũ không còn đúng.
                pendingScore = null;
                pendingSecondary = 0;
                sentScore = -1;
                sentSecondary = -1;
            }
            loadedForUid = uid;

            string raw = PlayerPrefs.GetString(prefsKey, string.Empty);
            string[] parts = raw.Split('|');
            if (parts.Length == 3 && parts[0] == uid
                && int.TryParse(parts[1], out int score) && int.TryParse(parts[2], out int secondary))
            {
                pendingScore = Math.Max(pendingScore ?? -1, score);
                pendingSecondary = Math.Max(pendingSecondary, secondary);
            }
        }

        private void SavePending()
        {
            if (!options.PersistPending || !pendingScore.HasValue)
                return;
            string uid = CurrentUid();
            if (string.IsNullOrEmpty(uid))
                return;
            PlayerPrefs.SetString(prefsKey, $"{uid}|{pendingScore.Value}|{pendingSecondary}");
            PlayerPrefs.Save();
        }

        private void ClearPending()
        {
            pendingScore = null;
            pendingSecondary = 0;
            if (options.PersistPending && PlayerPrefs.HasKey(prefsKey))
            {
                PlayerPrefs.DeleteKey(prefsKey);
                PlayerPrefs.Save();
            }
        }

        private string CurrentUid()
        {
            try
            {
                return client.Config.SelfUidProvider?.Invoke();
            }
            catch
            {
                return null;
            }
        }
    }
}
