using AbiGames.Leaderboard.UI;
using UnityEditor;
using UnityEngine;

namespace AbiGames.Leaderboard.Editor
{
    /// <summary>Menu dựng nhanh panel bảng xếp hạng vào scene đang mở — điểm bắt đầu để chỉnh theo game.</summary>
    public static class LeaderboardPanelMenu
    {
        [MenuItem("Tools/AbiGames/Leaderboard/Create Sample Panel")]
        public static void CreateSamplePanel()
        {
            Canvas canvas = null;
            if (Selection.activeGameObject != null)
                canvas = Selection.activeGameObject.GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                canvas = LeaderboardPanelFactory.CreateCanvas();
                Undo.RegisterCreatedObjectUndo(canvas.gameObject, "Create Leaderboard Canvas");
            }

            LeaderboardPanelFactory.Panel panel = LeaderboardPanelFactory.CreatePanel(canvas.transform);
            Undo.RegisterCreatedObjectUndo(panel.Root.gameObject, "Create Leaderboard Panel");
            Selection.activeGameObject = panel.Root.gameObject;
            EditorGUIUtility.PingObject(panel.Root.gameObject);
            Debug.Log("[Leaderboard] Đã dựng panel. Gọi LeaderboardRuntime.Initialize(...) lúc boot là panel tự tải khi bật. " +
                      "Kéo panel vào Project để lưu thành prefab.");
        }
    }
}
