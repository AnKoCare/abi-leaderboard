using System.Collections;
using AbiGames.Leaderboard.Testing;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace AbiGames.Leaderboard.Tests
{
    public class LeaderboardClientTests
    {
        private FakeLeaderboardTransport fake;

        [SetUp]
        public void SetUp()
        {
            fake = new FakeLeaderboardTransport();
            fake.AddPlayer(TestKit.SelfUid, 10, "vn", new { PlayerName = "An" });
            fake.SetToken(TestKit.Token, TestKit.SelfUid);
        }

        [UnityTest]
        public IEnumerator ChanLimitOffsetTrongGioiHanServer() => UniTask.ToCoroutine(async () =>
        {
            LeaderboardClient client = new LeaderboardClient(TestKit.Config(), fake);
            LeaderboardResult<LeaderboardPage> result = await client.GetTopPageAsync(" WW ", 5000, -3);
            Assert.IsTrue(result.IsSuccess);
            StringAssert.EndsWith("/api/v1/leaderboard/top?countryCode=ww&limit=1000&offset=0", fake.Requests[0].Url);
        });

        [UnityTest]
        public IEnumerator BoardSai_ChanNgayKhongGoiMang() => UniTask.ToCoroutine(async () =>
        {
            LeaderboardClient client = new LeaderboardClient(TestKit.Config(), fake);
            LeaderboardResult<LeaderboardPage> result = await client.GetTopPageAsync("vnm", 10, 0);
            Assert.AreEqual(LeaderboardErrorKind.InvalidArgument, result.Error.Kind);
            Assert.AreEqual(0, fake.Requests.Count);
        });

        [UnityTest]
        public IEnumerator GuiDiem_KemBearerAppVersionVaBody() => UniTask.ToCoroutine(async () =>
        {
            LeaderboardClient client = new LeaderboardClient(TestKit.Config(), fake);
            LeaderboardResult<LeaderboardSubmitResult> result = await client.SubmitScoreAsync(42);
            Assert.IsTrue(result.IsSuccess, result.Error?.ToString());
            Assert.AreEqual(42, result.Value.Score);
            LeaderboardHttpRequest request = fake.Requests[0];
            Assert.AreEqual("POST", request.Method);
            Assert.AreEqual("Bearer " + TestKit.Token, request.Headers["Authorization"]);
            Assert.IsTrue(request.Headers.ContainsKey("X-App-Version"));
            Assert.AreEqual("application/json", request.Headers["Content-Type"]);
            Assert.AreEqual("{\"score\":42}", request.Body);
            Assert.AreEqual(42, fake.Players[TestKit.SelfUid].Score);
        });

        [UnityTest]
        public IEnumerator ChuaDangNhap_KhongGoiMang() => UniTask.ToCoroutine(async () =>
        {
            LeaderboardClient client = new LeaderboardClient(TestKit.Config(token: () => null), fake);
            LeaderboardResult<LeaderboardSubmitResult> result = await client.SubmitScoreAsync(5);
            Assert.AreEqual(LeaderboardErrorKind.Unauthorized, result.Error.Kind);
            Assert.AreEqual(0, fake.Requests.Count);
        });

        [UnityTest]
        public IEnumerator Gap401_LamMoiTokenRoiThuLaiDungMotLan() => UniTask.ToCoroutine(async () =>
        {
            string token = "het-han";
            int refreshes = 0;
            LeaderboardConfig config = TestKit.Config(token: () => token);
            config.RefreshAccessToken = () =>
            {
                refreshes += 1;
                token = TestKit.Token;
                return UniTask.FromResult(true);
            };
            LeaderboardClient client = new LeaderboardClient(config, fake);

            LeaderboardResult<LeaderboardSubmitResult> result = await client.SubmitScoreAsync(77);
            Assert.IsTrue(result.IsSuccess, result.Error?.ToString());
            Assert.AreEqual(1, refreshes);
            Assert.AreEqual(2, fake.CountRequests("/submit"));
        });

        [UnityTest]
        public IEnumerator Gap401_KhongCoCachLamMoi_BaoUnauthorized() => UniTask.ToCoroutine(async () =>
        {
            LeaderboardClient client = new LeaderboardClient(TestKit.Config(token: () => "sai"), fake);
            LeaderboardResult<LeaderboardSubmitResult> result = await client.SubmitScoreAsync(5);
            Assert.AreEqual(LeaderboardErrorKind.Unauthorized, result.Error.Kind);
            Assert.AreEqual(401, result.Error.StatusCode);
            Assert.IsFalse(result.Error.IsRetryable);
        });

        [UnityTest]
        public IEnumerator UidKhongCoTrenBoard_ThanhCongVoiNull() => UniTask.ToCoroutine(async () =>
        {
            LeaderboardClient client = new LeaderboardClient(TestKit.Config(), fake);
            LeaderboardResult<LeaderboardRank> onVn = await client.GetRankAsync(TestKit.SelfUid, "vn");
            Assert.IsTrue(onVn.IsSuccess);
            Assert.AreEqual(1, onVn.Value.Rank);

            LeaderboardResult<LeaderboardRank> onUs = await client.GetRankAsync(TestKit.SelfUid, "us");
            Assert.IsTrue(onUs.IsSuccess, "body rỗng không phải lỗi");
            Assert.IsNull(onUs.Value);
        });

        [UnityTest]
        public IEnumerator PhanLoaiLoi() => UniTask.ToCoroutine(async () =>
        {
            LeaderboardClient client = new LeaderboardClient(TestKit.Config(), fake);
            fake.EnqueueResponse(503, "<html>503</html>");
            fake.EnqueueResponse(500, "{\"statusCode\":500,\"message\":\"Internal server error\"}");
            fake.EnqueueResponse(0, null, networkError: true);
            fake.EnqueueResponse(0, null, timeout: true);
            fake.EnqueueResponse(400, "{\"statusCode\":400,\"message\":[\"limit must not be greater than 1000\"]}");

            LeaderboardError rateLimited = (await client.GetTopPageAsync("ww", 10, 0)).Error;
            LeaderboardError server = (await client.GetTopPageAsync("ww", 10, 0)).Error;
            LeaderboardError network = (await client.GetTopPageAsync("ww", 10, 0)).Error;
            LeaderboardError timeout = (await client.GetTopPageAsync("ww", 10, 0)).Error;
            LeaderboardError badRequest = (await client.GetTopPageAsync("ww", 10, 0)).Error;

            Assert.AreEqual(LeaderboardErrorKind.RateLimited, rateLimited.Kind);
            Assert.AreEqual(LeaderboardErrorKind.Server, server.Kind);
            Assert.AreEqual("Internal server error", server.Message);
            Assert.AreEqual(LeaderboardErrorKind.Network, network.Kind);
            Assert.AreEqual(LeaderboardErrorKind.Timeout, timeout.Kind);
            Assert.AreEqual(LeaderboardErrorKind.BadRequest, badRequest.Kind);
            Assert.AreEqual("limit must not be greater than 1000", badRequest.Message);
            Assert.IsTrue(rateLimited.IsRetryable && server.IsRetryable && network.IsRetryable && timeout.IsRetryable);
            Assert.IsFalse(badRequest.IsRetryable);
        });

        [UnityTest]
        public IEnumerator HostBocVoSuccessData_VanDungDuoc() => UniTask.ToCoroutine(async () =>
        {
            fake.WrapInEnvelope = true;
            LeaderboardClient client = new LeaderboardClient(TestKit.Config(), fake);
            Assert.AreEqual(1, (await client.GetTopPageAsync("ww", 10, 0)).Value.Entries.Count);
            Assert.AreEqual(1, (await client.GetRankAsync(TestKit.SelfUid, "ww")).Value.Rank);
            Assert.IsNull((await client.GetRankAsync(TestKit.SelfUid, "jp")).Value);
        });

        [UnityTest]
        public IEnumerator ParseTrenThreadPool_TrangDu1000Dong() => UniTask.ToCoroutine(async () =>
        {
            TestKit.Seed(fake, 1200);
            LeaderboardConfig config = TestKit.Config();
            config.ParseOnThreadPool = true;
            LeaderboardClient client = new LeaderboardClient(config, fake);
            LeaderboardResult<LeaderboardPage> result = await client.GetTopPageAsync("ww", 1000, 0);
            Assert.IsTrue(result.IsSuccess, result.Error?.ToString());
            Assert.AreEqual(1000, result.Value.Entries.Count);
            Assert.AreEqual(1000, result.Value.BoardTotal, "board chỉ giữ tới topLimit");
            Assert.AreEqual(TestKit.Uid(1), result.Value.Entries[0].Uid);
            Assert.AreEqual(1000, result.Value.Entries[999].Rank);
        });
    }
}
