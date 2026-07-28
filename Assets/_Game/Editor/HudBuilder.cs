using Game.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Editor
{
    /// <summary>
    /// Patches the health bar + clock into the existing WorldUI HUD (does NOT regenerate
    /// WorldUI, so TabMenu/Console survive). Menu: Game/Setup/Build HUD. Idempotent.
    /// </summary>
    public static class HudBuilder
    {
        private const string WORLD_UI_PREFAB = "Assets/_Game/UI/WorldUI.prefab";

        private static readonly Color TextCol = new(0.85f, 0.85f, 0.80f, 1f);

        [MenuItem("Game/Setup/Build HUD")]
        public static void Build()
        {
            var root = PrefabUtility.LoadPrefabContents(WORLD_UI_PREFAB);
            try
            {
                var hud = root.transform.Find("HUD");
                if (hud == null)
                {
                    Debug.LogError("[HudBuilder] HUD not found in WorldUI - run Build Menus first.");
                    return;
                }

                var oldBar = hud.Find("HealthBar");
                if (oldBar != null) Object.DestroyImmediate(oldBar.gameObject);
                var oldClock = hud.Find("Clock");
                if (oldClock != null) Object.DestroyImmediate(oldClock.gameObject);

                // Health bar, bottom-left.
                var barGO = new GameObject("HealthBar", typeof(Image));
                barGO.transform.SetParent(hud, false);
                barGO.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.09f, 0.85f);
                barGO.GetComponent<Image>().raycastTarget = false;
                var barRt = barGO.GetComponent<RectTransform>();
                barRt.anchorMin = barRt.anchorMax = new Vector2(0, 0);
                barRt.pivot = new Vector2(0, 0);
                barRt.anchoredPosition = new Vector2(40, 40);
                barRt.sizeDelta = new Vector2(280, 18);

                var fillGO = new GameObject("Fill", typeof(Image));
                fillGO.transform.SetParent(barGO.transform, false);
                var fill = fillGO.GetComponent<Image>();
                fill.color = new Color(0.62f, 0.24f, 0.22f, 0.95f);
                fill.raycastTarget = false;
                fill.type = Image.Type.Filled;
                fill.fillMethod = Image.FillMethod.Horizontal;
                fill.fillAmount = 1f;
                var fillRt = fillGO.GetComponent<RectTransform>();
                fillRt.anchorMin = Vector2.zero;
                fillRt.anchorMax = Vector2.one;
                fillRt.offsetMin = new Vector2(2, 2);
                fillRt.offsetMax = new Vector2(-2, -2);

                // Clock, top-right.
                var clockGO = new GameObject("Clock", typeof(TextMeshProUGUI));
                clockGO.transform.SetParent(hud, false);
                var clock = clockGO.GetComponent<TextMeshProUGUI>();
                clock.text = "";
                clock.fontSize = 22;
                clock.color = TextCol;
                clock.alignment = TextAlignmentOptions.TopRight;
                clock.raycastTarget = false;
                var clockRt = clock.rectTransform;
                clockRt.anchorMin = clockRt.anchorMax = new Vector2(1, 1);
                clockRt.pivot = new Vector2(1, 1);
                clockRt.anchoredPosition = new Vector2(-40, -30);
                clockRt.sizeDelta = new Vector2(320, 34);

                var hudCtrl = hud.GetComponent<HUDController>();
                var so = new SerializedObject(hudCtrl);
                so.FindProperty("healthFill").objectReferenceValue = fill;
                so.FindProperty("clockLabel").objectReferenceValue = clock;
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, WORLD_UI_PREFAB);
                Debug.Log("[HudBuilder] Health bar + clock added to HUD.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
