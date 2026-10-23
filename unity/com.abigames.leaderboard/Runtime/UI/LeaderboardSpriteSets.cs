using System;
using System.Collections.Generic;
using UnityEngine;

namespace AbiGames.Leaderboard.UI
{
    /// <summary>Bộ avatar theo chỉ số — khớp với <c>info.AvatarIndex</c> của server.</summary>
    [CreateAssetMenu(menuName = "AbiGames/Leaderboard/Avatar Set", fileName = "LeaderboardAvatarSet")]
    public class LeaderboardAvatarSet : ScriptableObject
    {
        [field: SerializeField] public Sprite[] Sprites { get; private set; } = Array.Empty<Sprite>();

        [Tooltip("Dùng khi chỉ số nằm ngoài bộ (game thêm avatar mới mà bản cũ chưa có).")]
        [field: SerializeField] public Sprite Fallback { get; private set; }

        public Sprite Get(int index)
        {
            return Sprites != null && index >= 0 && index < Sprites.Length && Sprites[index] != null
                ? Sprites[index]
                : Fallback;
        }
    }

    /// <summary>Bộ cờ theo mã quốc gia ISO alpha-2 (viết thường).</summary>
    [CreateAssetMenu(menuName = "AbiGames/Leaderboard/Country Flag Set", fileName = "LeaderboardCountryFlagSet")]
    public class LeaderboardCountryFlagSet : ScriptableObject
    {
        [Serializable]
        public class Flag
        {
            public string Code;
            public Sprite Sprite;
        }

        [field: SerializeField] public List<Flag> Flags { get; private set; } = new List<Flag>();
        [field: SerializeField] public Sprite Fallback { get; private set; }

        private Dictionary<string, Sprite> lookup;

        public Sprite Get(string countryCode)
        {
            string code = LeaderboardBoards.Normalize(countryCode);
            if (code == null)
                return Fallback;
            if (lookup == null)
            {
                lookup = new Dictionary<string, Sprite>();
                foreach (Flag flag in Flags)
                {
                    string key = LeaderboardBoards.Normalize(flag?.Code);
                    if (key != null && flag.Sprite != null)
                        lookup[key] = flag.Sprite;
                }
            }
            return lookup.TryGetValue(code, out Sprite sprite) ? sprite : Fallback;
        }

        private void OnValidate() => lookup = null;
    }
}
