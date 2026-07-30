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
                var oldRefuel = hud.Find("RefuelPanel");
                if (oldRefuel != null) Object.DestroyImmediate(oldRefuel.gameObject);

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

                // Refuel meter: two stacked bars, centred low on screen - above the interact
                // prompt at y=0.32, out of the way of both the crosshair and the health bar.
                // Hidden by default; HUDController shows it only while FuelTank.LocalActive
                // is set.
                var panelGO = new GameObject("RefuelPanel");
                panelGO.transform.SetParent(hud, false);
                var panelRt = panelGO.AddComponent<RectTransform>();
                panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.40f);
                panelRt.pivot = new Vector2(0.5f, 0f);
                panelRt.sizeDelta = new Vector2(340, 64);
                panelGO.SetActive(false);

                var (containerFill, containerLabel) = MeterRow(panelRt, "Container", new Vector2(0, 34));
                var (tankFill, tankLabel) = MeterRow(panelRt, "Tank", new Vector2(0, 2));

                var hudCtrl = hud.GetComponent<HUDController>();
                var so = new SerializedObject(hudCtrl);
                so.FindProperty("healthFill").objectReferenceValue = fill;
                so.FindProperty("clockLabel").objectReferenceValue = clock;
                so.FindProperty("refuelPanel").objectReferenceValue = panelGO;
                so.FindProperty("refuelContainerFill").objectReferenceValue = containerFill;
                so.FindProperty("refuelContainerLabel").objectReferenceValue = containerLabel;
                so.FindProperty("refuelTankFill").objectReferenceValue = tankFill;
                so.FindProperty("refuelTankLabel").objectReferenceValue = tankLabel;
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, WORLD_UI_PREFAB);
                Debug.Log("[HudBuilder] Health bar + clock added to HUD.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// One bar of the refuel meter: a background, a horizontal fill, and a label overlaid
        /// on top showing the live numbers - the same background+fill construction as the
        /// health bar above, plus text since a percentage alone does not tell a player whether
        /// they are watching a 5 L jerry can or a 140 L ship's tank.
        /// </summary>
        private static (Image fill, TextMeshProUGUI label) MeterRow(Transform parent, string name,
            Vector2 anchoredPos)
        {
            var barGO = new GameObject(name + "Bar", typeof(Image));
            barGO.transform.SetParent(parent, false);
            barGO.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.09f, 0.85f);
            barGO.GetComponent<Image>().raycastTarget = false;
            var barRt = barGO.GetComponent<RectTransform>();
            barRt.anchorMin = barRt.anchorMax = new Vector2(0.5f, 0f);
            barRt.pivot = new Vector2(0.5f, 0f);
            barRt.anchoredPosition = anchoredPos;
            barRt.sizeDelta = new Vector2(320, 28);

            var fillGO = new GameObject("Fill", typeof(Image));
            fillGO.transform.SetParent(barGO.transform, false);
            var fill = fillGO.GetComponent<Image>();
            fill.color = new Color(0.42f, 0.62f, 0.30f, 0.95f);
            fill.raycastTarget = false;
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillAmount = 0f;
            var fillRt = fillGO.GetComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = new Vector2(2, 2);
            fillRt.offsetMax = new Vector2(-2, -2);

            var labelGO = new GameObject("Label", typeof(TextMeshProUGUI));
            labelGO.transform.SetParent(barGO.transform, false);
            var label = labelGO.GetComponent<TextMeshProUGUI>();
            label.text = "";
            label.fontSize = 16;
            label.color = TextCol;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            var labelRt = label.rectTransform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;

            return (fill, label);
        }
    }
}
