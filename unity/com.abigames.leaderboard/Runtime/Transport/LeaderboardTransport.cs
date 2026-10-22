using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;

namespace AbiGames.Leaderboard
{
    public sealed class LeaderboardHttpRequest
    {
        public string Method;
        public string Url;
        /// <summary>JSON; null = không có body.</summary>
        public string Body;
        public IReadOnlyDictionary<string, string> Headers;
        public int TimeoutSeconds;
    }

    public sealed class LeaderboardHttpResponse
    {
        /// <summary>Mã HTTP; 0 = không có response (mất mạng, timeout).</summary>
        public long StatusCode;
        public string Body;
        /// <summary>Không tới được server.</summary>
        public bool IsNetworkError;
        public bool IsTimeout;
        public string Error;
    }

    /// <summary>
    /// Lớp gửi HTTP. Mặc định <see cref="UnityWebRequestTransport"/>; test và bản demo dùng
    /// <see cref="Testing.FakeLeaderboardTransport"/>. Game muốn đi chung client HTTP của
    /// mình (retry, log, chứng chỉ riêng...) thì tự cài interface này.
    /// </summary>
    public interface ILeaderboardTransport
    {
        UniTask<LeaderboardHttpResponse> SendAsync(LeaderboardHttpRequest request, CancellationToken cancellationToken);
    }

    public sealed class UnityWebRequestTransport : ILeaderboardTransport
    {
        public async UniTask<LeaderboardHttpResponse> SendAsync(LeaderboardHttpRequest request, CancellationToken cancellationToken)
        {
            using (UnityWebRequest web = new UnityWebRequest(request.Url, request.Method))
            {
                if (request.Body != null)
                    web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.Body));
                web.downloadHandler = new DownloadHandlerBuffer();
                web.timeout = Math.Max(1, request.TimeoutSeconds);
                if (request.Headers != null)
                {
                    foreach (KeyValuePair<string, string> header in request.Headers)
                    {
                        if (!string.IsNullOrEmpty(header.Key) && header.Value != null)
                            web.SetRequestHeader(header.Key, header.Value);
                    }
                }

                try
                {
                    await web.SendWebRequest().WithCancellation(cancellationToken);
                }
                catch (UnityWebRequestException)
                {
                    // UniTask ném khi HTTP lỗi hoặc mất mạng; trạng thái đọc từ `web` ngay dưới.
                    // Huỷ (OperationCanceledException) thì để nổi lên — UniTask đã Abort request.
                }

                bool networkError = web.result == UnityWebRequest.Result.ConnectionError;
                string error = web.error;
                return new LeaderboardHttpResponse
                {
                    StatusCode = web.responseCode,
                    Body = web.downloadHandler?.text,
                    IsNetworkError = networkError,
                    IsTimeout = networkError && error != null && error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0,
                    Error = error,
                };
            }
        }
    }
}
