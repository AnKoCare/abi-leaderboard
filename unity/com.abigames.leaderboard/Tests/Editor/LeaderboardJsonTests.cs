using NUnit.Framework;

namespace AbiGames.Leaderboard.Tests
{
    public class LeaderboardJsonTests
    {
        private const string PageJson =
            "{\"countryCode\":\"ww\",\"total\":2,\"boardTotal\":1000,\"entries\":[" +
            "{\"rank\":1,\"uid\":\"1000000002\",\"score\":50,\"countryCode\":\"us\",\"secondaryScore\":3,\"info\":{\"PlayerName\":\"Binh\",\"Level\":40}}," +
            "{\"rank\":2,\"uid\":\"1000000001\",\"score\":50,\"countryCode\":null,\"info\":{\"PlayerName\":\"An\",\"Level\":\"12\",\"AvatarIndex\":null}}]}";

        [Test]
        public void DocDungTrangCuaServer()
        {
            Assert.IsTrue(LeaderboardJson.TryParse(PageJson, out LeaderboardPage page, out string error), error);
            Assert.AreEqual(1000, page.BoardTotal);
            Assert.AreEqual(2, page.Entries.Count);
            Assert.AreEqual(3, page.Entries[0].SecondaryScore);
            Assert.AreEqual(0, page.Entries[1].SecondaryScore, "server bỏ trống = 0");
            Assert.IsNull(page.Entries[1].CountryCode);
            Assert.AreEqual("Binh", page.Entries[0].GetInfoString("PlayerName"));
        }

        [Test]
        public void ServerCuKhongCoBoardTotal_ThiChiBietDuocTrangNay()
        {
            Assert.IsTrue(LeaderboardJson.TryParse("{\"countryCode\":\"vn\",\"total\":7,\"entries\":[]}", out LeaderboardPage page, out _));
            Assert.AreEqual(7, page.KnownBoardTotal);
        }

        [Test]
        public void BoVoSuccessData()
        {
            string wrapped = "{\"success\":true,\"message\":\"\",\"data\":" + PageJson + "}";
            Assert.IsTrue(LeaderboardJson.TryParse(wrapped, out LeaderboardPage page, out _));
            Assert.AreEqual(2, page.Entries.Count);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("null")]
        [TestCase(null)]
        [TestCase("{\"success\":true,\"data\":null}")]
        public void BodyRong_LaThanhCongKhongCoDuLieu(string body)
        {
            Assert.IsTrue(LeaderboardJson.TryParse(body, out LeaderboardRank rank, out _));
            Assert.IsNull(rank);
        }

        [Test]
        public void BodyHong_BaoLoiParse()
        {
            Assert.IsFalse(LeaderboardJson.TryParse("{khong phai json", out LeaderboardPage page, out string error));
            Assert.IsNull(page);
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void DocCauBaoLoiCuaNest_KeCaMangLoiValidate()
        {
            Assert.AreEqual("score must not be less than 0; extra should not exist",
                LeaderboardJson.ReadErrorMessage("{\"statusCode\":400,\"message\":[\"score must not be less than 0\",\"extra should not exist\"],\"error\":\"Bad Request\"}", "x"));
            Assert.AreEqual("Unauthorized", LeaderboardJson.ReadErrorMessage("{\"statusCode\":401,\"message\":\"Unauthorized\"}", "x"));
            Assert.AreEqual("fallback", LeaderboardJson.ReadErrorMessage("<html>502</html>", "fallback"));
        }

        [Test]
        public void DocInfo_ThieuKeyHoacSaiKieuThiLayMacDinh()
        {
            LeaderboardJson.TryParse(PageJson, out LeaderboardPage page, out _);
            LeaderboardEntry an = page.Entries[1];
            Assert.AreEqual(12, an.GetInfoInt("Level"), "số gửi dạng chuỗi vẫn đọc được");
            Assert.AreEqual(-1, an.GetInfoInt("AvatarIndex", -1), "null → mặc định");
            Assert.AreEqual(-1, an.GetInfoInt("KhongCo", -1));
            Assert.AreEqual("?", an.GetInfoString("KhongCo", "?"));
            Assert.AreEqual(0, new LeaderboardEntry().GetInfoInt("Level"), "info null vẫn an toàn");
        }

        private class Profile
        {
            public string PlayerName { get; set; }
            public int Level { get; set; }
        }

        [Test]
        public void DocInfoThanhClassCuaGame()
        {
            LeaderboardJson.TryParse(PageJson, out LeaderboardPage page, out _);
            Profile p = page.Entries[0].InfoAs<Profile>();
            Assert.AreEqual("Binh", p.PlayerName);
            Assert.AreEqual(40, p.Level);
            Assert.IsNull(new LeaderboardEntry().InfoAs<Profile>());
        }
    }
}
