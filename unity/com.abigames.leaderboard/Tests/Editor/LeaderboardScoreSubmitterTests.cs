using System.Collections;
using System.Collections.Generic;
using AbiGames.Leaderboard.Testing;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AbiGames.Leaderboard.Tests
{
    public class LeaderboardScoreSubmitterTests
    {
        private FakeLeaderboardTransport fake;
        private string storageKey;
        private readonly List<LeaderboardScoreSubmitter> created = new List<LeaderboardScoreSubmitter>();

        [SetUp]
        public void SetUp()
        {
            fake = new FakeLeaderboardTransport();
            fake.AddPlayer(TestKit.SelfUid, 0, "vn");
            fake.SetToken(TestKit.Token, TestKit.SelfUid);
            storageKey = "submit-test-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (LeaderboardScoreSubmitter s in created)
                s.Dispose();
            created.Clear();
            PlayerPrefs.DeleteKey("abi_lb.pending." + storageKey);
        }

        private LeaderboardScoreSubmitter Submitter(LeaderboardConfig config = null, int maxAttempts = 5)
        {
            LeaderboardScoreSubmitter s = new LeaderboardScoreSubmitter(
                new LeaderboardClient(config ?? TestKit.Config(storageKey: storageKey), fake),
                new LeaderboardSubmitterOptions { MaxAttempts = maxAttempts, FirstRetryDelaySeconds = 0.01f });
            created.Add(s);
            return s;
        }

        private static UniTask Idle(LeaderboardScoreSubmitter s) => TestKit.WaitUntil(() => !s.IsSending);

        [UnityTest]
        public IEnumerator GuiDon_ChiGuiSoLonNhat() => UniTask.ToCoroutine(async () =>
        {
            fake.LatencySeconds = 0.05f;
            LeaderboardScoreSubmitter s = Submitter();
            s.Submit(5);
            s.Submit(9);
            s.Submit(7);
            await TestKit.WaitUntil(() => !s.IsSending && !s.HasPending);

            Assert.AreEqual(9, fake.Players[TestKit.SelfUid].Score);
            Assert.LessOrEqual(fake.CountRequests("/submit"), 2, "5 đang bay, sau đó chỉ gửi 9");
            Assert.AreEqual(9, s.HighestSentScore);
        });

        [UnityTest]
        public IEnumerator DiemKhongCaoHon_KhongGuiNua() => UniTask.ToCoroutine(async () =>
        {
            LeaderboardScoreSubmitter s = Submitter();
            s.Submit(9);
            await Idle(s);
            int sent = fake.CountRequests("/submit");
            s.Submit(9);
            s.Submit(3);
            await UniTask.Yield();
            Assert.AreEqual(sent, fake.CountRequests("/submit"));
            Assert.IsFalse(s.HasPending);
        });

        [UnityTest]
        public IEnumerator LoiTamThoi_ThuLaiRoiThanhCong() => UniTask.ToCoroutine(async () =>
        {
            fake.EnqueueFailures(2, 503);
            LeaderboardScoreSubmitter s = Submitter();
            int submitted = 0;
            s.Submitted += _ => submitted += 1;
            s.Submit(12);
            await TestKit.WaitUntil(() => !s.IsSending && !s.HasPending);

            Assert.AreEqual(3, fake.CountRequests("/submit"));
            Assert.AreEqual(1, submitted);
            Assert.AreEqual(12, fake.Players[TestKit.SelfUid].Score);
        });

        [UnityTest]
        public IEnumerator HetLuotThu_GiuDiemDeGuiSau() => UniTask.ToCoroutine(async () =>
        {
            fake.EnqueueFailures(3, 502);
            LeaderboardScoreSubmitter s = Submitter(maxAttempts: 3);
            LeaderboardError failed = null;
            s.Failed += e => failed = e;
            s.Submit(20);
            await TestKit.WaitUntil(() => failed != null);
            await Idle(s);

            Assert.AreEqual(LeaderboardErrorKind.Server, failed.Kind);
            Assert.IsTrue(s.HasPending);
            Assert.AreEqual(0, fake.Players[TestKit.SelfUid].Score);

            s.ResendPending();
            await TestKit.WaitUntil(() => !s.IsSending && !s.HasPending);
            Assert.AreEqual(20, fake.Players[TestKit.SelfUid].Score);
        });

        [UnityTest]
        public IEnumerator BiCam_BoDiemKhongThuLai() => UniTask.ToCoroutine(async () =>
        {
            fake.Players[TestKit.SelfUid].Blocked = true;
            LeaderboardScoreSubmitter s = Submitter();
            LeaderboardError failed = null;
            s.Failed += e => failed = e;
            s.Submit(30);
            await TestKit.WaitUntil(() => failed != null);

            Assert.AreEqual(LeaderboardErrorKind.Forbidden, failed.Kind);
            Assert.IsFalse(s.HasPending);
            Assert.AreEqual(1, fake.CountRequests("/submit"));
        });

        [UnityTest]
        public IEnumerator ChuaDangNhap_GiuDiem_DangNhapXongThiGui() => UniTask.ToCoroutine(async () =>
        {
            string token = "chua-co";
            LeaderboardScoreSubmitter s = Submitter(TestKit.Config(token: () => token, storageKey: storageKey));
            LeaderboardError failed = null;
            s.Failed += e => failed = e;
            s.Submit(15);
            await TestKit.WaitUntil(() => failed != null);
            Assert.AreEqual(LeaderboardErrorKind.Unauthorized, failed.Kind);
            Assert.IsTrue(s.HasPending);

            token = TestKit.Token;
            s.ResendPending();
            await TestKit.WaitUntil(() => !s.IsSending && !s.HasPending);
            Assert.AreEqual(15, fake.Players[TestKit.SelfUid].Score);
        });

        [UnityTest]
        public IEnumerator TatGameGiuaChung_LanMoSauGuiTiep() => UniTask.ToCoroutine(async () =>
        {
            fake.EnqueueResponse(0, null, networkError: true);
            LeaderboardScoreSubmitter first = Submitter(maxAttempts: 1);
            first.Submit(44);
            await Idle(first);
            Assert.IsTrue(first.HasPending);
            first.Dispose();

            LeaderboardScoreSubmitter second = Submitter();
            second.ResendPending();
            await TestKit.WaitUntil(() => !second.IsSending && !second.HasPending);
            Assert.AreEqual(44, fake.Players[TestKit.SelfUid].Score);
            Assert.IsFalse(PlayerPrefs.HasKey("abi_lb.pending." + storageKey), "gửi xong thì xoá");
        });

        [UnityTest]
        public IEnumerator DiemChoCuaTaiKhoanKhac_KhongGuiNham() => UniTask.ToCoroutine(async () =>
        {
            PlayerPrefs.SetString("abi_lb.pending." + storageKey, "1999999999|500|0");
            LeaderboardScoreSubmitter s = Submitter();
            s.ResendPending();
            await UniTask.Yield();
            Assert.AreEqual(0, fake.CountRequests("/submit"));
            Assert.IsFalse(s.HasPending);
        });
    }
}
