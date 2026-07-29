using System.IO;
using Game.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Editor
{
    /// <summary>
    /// Generates the UGUI menu prefabs (MainMenuUI, WorldUI, SettingsPanel, row prefabs)
    /// and drops instances into the MainMenu/World scenes. Idempotent by regeneration:
    /// re-running rebuilds the prefabs from scratch, so hand-edits belong in derived
    /// prefabs or after the layout stabilizes. Runtime code never builds UI.
    ///
    /// Menu: Game/Setup/Build Menus
    /// </summary>
    public static class MenuBuilder
    {
        private const string UI_FOLDER = "Assets/_Game/UI";
        private const string INPUT_ACTIONS_PATH = "Assets/_Game/Settings/GameInputActions.inputactions";
        private const string MAINMENU_SCENE = "Assets/_Game/Scenes/MainMenu.unity";
        private const string WORLD_SCENE = "Assets/_Game/Scenes/World.unity";

        // Flat PSX-horror palette.
        private static readonly Color ScreenBg = new(0.030f, 0.030f, 0.038f, 0.98f);
        private static readonly Color PanelBg = new(0.075f, 0.075f, 0.090f, 1f);
        private static readonly Color ControlBg = new(0.125f, 0.125f, 0.150f, 1f);
        private static readonly Color TextCol = new(0.85f, 0.85f, 0.80f, 1f);
        private static readonly Color DimText = new(0.55f, 0.55f, 0.52f, 1f);
        private static readonly Color Accent = new(0.58f, 0.66f, 0.55f, 1f);
        /// <summary>Title colour. Light rather than blood red - it has to sit on a dark
        /// night scene and stay legible without glowing like an error message.</summary>
        private static readonly Color TitleCol = new(0.93f, 0.42f, 0.40f, 1f);
        private const string SCRIM_PATH = UI_FOLDER + "/MenuScrim.png";

        /// <summary>Menu typeface, resolved once per build. Null falls back to the TMP default.</summary>
        private static TMP_FontAsset _font;

        [MenuItem("Game/Setup/Build Menus")]
        public static void Build()
        {
            if (!MenuSceneBuilder.Ready("MenuBuilder")) return;

            EnsureTmpEssentials();
            EnsureFolder(UI_FOLDER);
            _font = MenuFont.Ensure();

            var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(INPUT_ACTIONS_PATH);
            if (actions == null)
            {
                Debug.LogError($"[MenuBuilder] Missing input actions at {INPUT_ACTIONS_PATH}");
                return;
            }

            var slotRow = BuildSlotRowPrefab();
            var lobbyRow = BuildLobbyRowPrefab();
            var rebindRow = BuildRebindRowPrefab();
            var settingsPanel = BuildSettingsPanelPrefab(actions, rebindRow);
            BuildMainMenuPrefab(settingsPanel, slotRow, lobbyRow);
            BuildWorldUiPrefab(settingsPanel, actions);
            UpdateMainMenuScene(actions);
            UpdateWorldScene(actions);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[MenuBuilder] Menus built. Play Boot to see the real main menu.");
        }

        private static void EnsureTmpEssentials()
        {
            if (TMP_Settings.instance == null)
            {
                TMP_PackageResourceImporter.ImportResources(true, false, false);
                AssetDatabase.Refresh();
            }
        }

        // ================= row prefabs =================

        private static GameObject BuildSlotRowPrefab()
        {
            var root = Row(null, "SlotRow", 56);
            root.GetComponent<Image>().color = PanelBg;

            var label = Text(root.transform, "Label", "Slot", 20, TextAlignmentOptions.MidlineLeft);
            Flex(label.gameObject, 1f);
            var (primary, primaryText) = TextButton(root.transform, "PrimaryButton", "New", 150, 44);
            var (delete, _) = TextButton(root.transform, "DeleteButton", "Delete", 100, 44);

            var row = root.AddComponent<SlotRowUI>();
            Wire(row, "label", label);
            Wire(row, "primaryButton", primary);
            Wire(row, "primaryLabel", primaryText);
            Wire(row, "deleteButton", delete);

            return SavePrefab(root, $"{UI_FOLDER}/SlotRow.prefab");
        }

        private static GameObject BuildLobbyRowPrefab()
        {
            var root = Row(null, "LobbyRow", 52);
            root.GetComponent<Image>().color = PanelBg;

            var label = Text(root.transform, "Label", "Lobby", 20, TextAlignmentOptions.MidlineLeft);
            Flex(label.gameObject, 1f);
            var (join, _) = TextButton(root.transform, "JoinButton", "Join", 120, 40);

            var row = root.AddComponent<LobbyRowUI>();
            Wire(row, "label", label);
            Wire(row, "joinButton", join);

            return SavePrefab(root, $"{UI_FOLDER}/LobbyRow.prefab");
        }

        private static GameObject BuildRebindRowPrefab()
        {
            var root = Row(null, "RebindRow", 42);
            root.GetComponent<Image>().color = new Color(0, 0, 0, 0);

            var action = Text(root.transform, "ActionLabel", "Action", 18, TextAlignmentOptions.MidlineLeft);
            Flex(action.gameObject, 1f);
            var binding = Text(root.transform, "BindingLabel", "Key", 18, TextAlignmentOptions.Midline);
            binding.color = Accent;
            Fixed(binding.gameObject, 160);
            var (rebind, _) = TextButton(root.transform, "RebindButton", "Rebind", 110, 36);

            var row = root.AddComponent<RebindRow>();
            Wire(row, "actionLabel", action);
            Wire(row, "bindingLabel", binding);
            Wire(row, "rebindButton", rebind);

            return SavePrefab(root, $"{UI_FOLDER}/RebindRow.prefab");
        }

        // ================= settings panel =================

        private static GameObject BuildSettingsPanelPrefab(InputActionAsset actions, GameObject rebindRowPrefab)
        {
            var (root, column) = Screen("SettingsPanel", 760);
            Text(column, "Title", "SETTINGS", 40, TextAlignmentOptions.Center);

            // Tab bar
            var tabBar = Row(column, "TabBar", 48).transform;
            tabBar.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            var (gfxTab, _) = TextButton(tabBar, "GraphicsTab", "Graphics", 0, 44); Flex(gfxTab.gameObject, 1f);
            var (audTab, _) = TextButton(tabBar, "AudioTab", "Audio", 0, 44); Flex(audTab.gameObject, 1f);
            var (ctlTab, _) = TextButton(tabBar, "ControlsTab", "Controls", 0, 44); Flex(ctlTab.gameObject, 1f);

            // ---- Graphics tab ----
            var gfx = Column(column, "GraphicsPanel", 8);
            var quality = LabeledDropdown(gfx, "Quality preset", out var qualityDd);
            var res = LabeledDropdown(gfx, "Resolution", out var resDd);
            var win = LabeledDropdown(gfx, "Window mode", out var winDd);
            var vsync = LabeledToggle(gfx, "VSync", out var vsyncTg);
            var fovLabel = Text(gfx, "FovLabel", "Field of view: 75", 18, TextAlignmentOptions.MidlineLeft);
            var fov = SliderControl(gfx, "FovSlider", 50, 110, true);
            var psx = LabeledDropdown(gfx, "PSX internal resolution", out var psxDd);
            var aa = LabeledDropdown(gfx, "Anti-aliasing", out var aaDd);
            var mb = LabeledToggle(gfx, "Motion blur", out var mbTg);
            var grain = LabeledToggle(gfx, "Film grain", out var grainTg);

            // ---- Audio tab ----
            var aud = Column(column, "AudioPanel", 8);
            Text(aud, "MasterLabel", "Master volume", 18, TextAlignmentOptions.MidlineLeft);
            var master = SliderControl(aud, "MasterSlider", 0, 1, false);
            Text(aud, "MusicLabel", "Music volume", 18, TextAlignmentOptions.MidlineLeft);
            var music = SliderControl(aud, "MusicSlider", 0, 1, false);
            Text(aud, "SfxLabel", "Effects volume", 18, TextAlignmentOptions.MidlineLeft);
            var sfx = SliderControl(aud, "SfxSlider", 0, 1, false);

            // ---- Controls tab ----
            var ctl = Column(column, "ControlsPanel", 6);
            var sensLabel = Text(ctl, "SensitivityLabel", "Mouse sensitivity: 1.00", 18, TextAlignmentOptions.MidlineLeft);
            var sens = SliderControl(ctl, "SensitivitySlider", 0.1f, 3f, false);
            var invert = LabeledToggle(ctl, "Invert Y axis", out var invertTg);
            var rebindContainer = Column(ctl, "RebindContainer", 4);
            var (resetB, _) = TextButton(ctl.transform, "ResetBindings", "Reset key bindings", 0, 40);

            var (back, _) = TextButton(column, "BackButton", "Back", 0, 48);

            var screen = root.AddComponent<SettingsScreen>();
            Wire(screen, "graphicsTabButton", gfxTab);
            Wire(screen, "audioTabButton", audTab);
            Wire(screen, "controlsTabButton", ctlTab);
            Wire(screen, "graphicsPanel", gfx.gameObject);
            Wire(screen, "audioPanel", aud.gameObject);
            Wire(screen, "controlsPanel", ctl.gameObject);
            Wire(screen, "qualityDropdown", qualityDd);
            Wire(screen, "resolutionDropdown", resDd);
            Wire(screen, "windowModeDropdown", winDd);
            Wire(screen, "vsyncToggle", vsyncTg);
            Wire(screen, "fovSlider", fov);
            Wire(screen, "fovLabel", fovLabel);
            Wire(screen, "psxResDropdown", psxDd);
            Wire(screen, "aaDropdown", aaDd);
            Wire(screen, "motionBlurToggle", mbTg);
            Wire(screen, "filmGrainToggle", grainTg);
            Wire(screen, "masterSlider", master);
            Wire(screen, "musicSlider", music);
            Wire(screen, "sfxSlider", sfx);
            Wire(screen, "sensitivitySlider", sens);
            Wire(screen, "sensitivityLabel", sensLabel);
            Wire(screen, "invertYToggle", invertTg);
            Wire(screen, "rebindContainer", rebindContainer.transform);
            Wire(screen, "rebindRowPrefab", rebindRowPrefab.GetComponent<RebindRow>());
            Wire(screen, "resetBindingsButton", resetB);
            Wire(screen, "inputActions", actions);
            Wire(screen, "backButton", back);

            return SavePrefab(root, $"{UI_FOLDER}/SettingsPanel.prefab");
        }

        // ================= main menu =================

        private static void BuildMainMenuPrefab(GameObject settingsPanelPrefab, GameObject slotRowPrefab, GameObject lobbyRowPrefab)
        {
            var canvasGo = CanvasRoot("MainMenuUI");

            // --- Title screen ---
            // The only screen that is NOT an opaque panel: it sits over the live harbour in
            // the MainMenu scene, so it gets a left-hand scrim for legibility and leaves the
            // right two thirds of the frame to the water, the pier and the boat.
            var (title, titleCol) = Screen("TitleScreen", 460, canvasGo.transform, showcase: true);

            var titleText = Text(titleCol, "GameTitle", "F A B L E", 88, TextAlignmentOptions.Left);
            titleText.color = TitleCol;
            Text(titleCol, "Subtitle", "a cold place", 20, TextAlignmentOptions.Left).color = DimText;
            Spacer(titleCol, 40);
            var (playB, playL) = TextButton(titleCol, "PlayButton", "Play", 320, 54);
            var (joinB, joinL) = TextButton(titleCol, "JoinButton", "Join Game", 320, 54);
            var (setB, setL) = TextButton(titleCol, "SettingsButton", "Settings", 320, 54);
            var (quitB, quitL) = TextButton(titleCol, "QuitButton", "Quit", 320, 54);
            foreach (var label in new[] { playL, joinL, setL, quitL })
            {
                label.alignment = TextAlignmentOptions.Left;
                label.margin = new Vector4(20f, 0f, 0f, 0f);
            }

            // --- Save slots screen ---
            var (slots, slotsCol) = Screen("SaveSlotsScreen", 700, canvasGo.transform);
            Text(slotsCol, "Title", "SELECT SAVE", 40, TextAlignmentOptions.Center);
            var slotContainer = Column(slotsCol, "SlotContainer", 8);
            var (slotsBack, _) = TextButton(slotsCol, "BackButton", "Back", 0, 48);

            // --- Game setup screen ---
            var (setup, setupCol) = Screen("GameSetupScreen", 640, canvasGo.transform);
            Text(setupCol, "Title", "GAME SETUP", 40, TextAlignmentOptions.Center);
            var nameField = LabeledInput(setupCol, "Session name", out var nameInput);
            LabeledDropdown(setupCol, "Difficulty", out var diffDd);
            diffDd.ClearOptions();
            diffDd.AddOptions(new System.Collections.Generic.List<string> { "Easy", "Normal", "Hard", "Hardcore" });
            LabeledDropdown(setupCol, "Visibility", out var visDd);
            visDd.ClearOptions();
            visDd.AddOptions(new System.Collections.Generic.List<string> { "Solo", "Friends (code only)", "Public" });
            var mpLabel = Text(setupCol, "MaxPlayersLabel", "Max players: 4", 18, TextAlignmentOptions.MidlineLeft);
            var mpSlider = SliderControl(setupCol, "MaxPlayersSlider", 2, 4, true);
            mpSlider.value = 4;
            var dlLabel = Text(setupCol, "DayLengthLabel", "Day length: 30 min", 18, TextAlignmentOptions.MidlineLeft);
            var dlSlider = SliderControl(setupCol, "DayLengthSlider", 10, 60, true);
            dlSlider.value = 30;
            LabeledToggle(setupCol, "Friendly fire", out var ffToggle);
            var setupStatus = Text(setupCol, "Status", "", 16, TextAlignmentOptions.Center);
            setupStatus.color = DimText;
            var (startB, startText) = TextButton(setupCol, "StartButton", "Start", 0, 54);
            var (setupBack, _) = TextButton(setupCol, "BackButton", "Back", 0, 48);

            // --- Lobby browser screen ---
            var (browser, browserCol) = Screen("LobbyBrowserScreen", 700, canvasGo.transform);
            Text(browserCol, "Title", "JOIN GAME", 40, TextAlignmentOptions.Center);
            var (refreshB, _) = TextButton(browserCol, "RefreshButton", "Refresh", 0, 44);
            var listContainer = ScrollList(browserCol, "LobbyList", 360);
            var browserStatus = Text(browserCol, "Status", "", 16, TextAlignmentOptions.Center);
            browserStatus.color = DimText;
            var codeRow = Row(browserCol, "CodeRow", 52).transform;
            codeRow.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            var codeInput = InputControl(codeRow, "CodeInput", "lobby code");
            Flex(codeInput.gameObject, 1f);
            var (codeJoinB, _) = TextButton(codeRow, "JoinByCodeButton", "Join by Code", 200, 48);
            var (browserBack, _) = TextButton(browserCol, "BackButton", "Back", 0, 48);

            // --- Settings (nested prefab instance) ---
            var settingsInstance = (GameObject)PrefabUtility.InstantiatePrefab(settingsPanelPrefab, canvasGo.transform);
            Stretch(settingsInstance.GetComponent<RectTransform>());
            var settingsScreen = settingsInstance.GetComponent<SettingsScreen>();

            // Controllers + wiring
            var manager = canvasGo.AddComponent<MenuScreenManager>();
            var titleScreen = title.AddComponent<TitleScreen>();
            var slotsScreen = slots.AddComponent<SaveSlotsScreen>();
            var setupScreen = setup.AddComponent<GameSetupScreen>();
            var browserScreen = browser.AddComponent<LobbyBrowserScreen>();

            Wire(manager, "initialScreen", titleScreen);
            Wire(manager, "settingsScreen", settingsScreen);

            Wire(titleScreen, "playButton", playB);
            Wire(titleScreen, "joinButton", joinB);
            Wire(titleScreen, "settingsButton", setB);
            Wire(titleScreen, "quitButton", quitB);
            Wire(titleScreen, "saveSlotsScreen", slotsScreen);
            Wire(titleScreen, "lobbyBrowserScreen", browserScreen);
            Wire(titleScreen, "settingsScreen", settingsScreen);

            Wire(slotsScreen, "slotContainer", slotContainer.transform);
            Wire(slotsScreen, "slotRowPrefab", slotRowPrefab.GetComponent<SlotRowUI>());
            Wire(slotsScreen, "backButton", slotsBack);
            Wire(slotsScreen, "gameSetupScreen", setupScreen);

            Wire(setupScreen, "nameInput", nameInput);
            Wire(setupScreen, "difficultyDropdown", diffDd);
            Wire(setupScreen, "visibilityDropdown", visDd);
            Wire(setupScreen, "maxPlayersSlider", mpSlider);
            Wire(setupScreen, "maxPlayersLabel", mpLabel);
            Wire(setupScreen, "dayLengthSlider", dlSlider);
            Wire(setupScreen, "dayLengthLabel", dlLabel);
            Wire(setupScreen, "friendlyFireToggle", ffToggle);
            Wire(setupScreen, "startButton", startB);
            Wire(setupScreen, "startLabel", startText);
            Wire(setupScreen, "statusLabel", setupStatus);
            Wire(setupScreen, "backButton", setupBack);

            Wire(browserScreen, "listContainer", listContainer.transform);
            Wire(browserScreen, "lobbyRowPrefab", lobbyRowPrefab.GetComponent<LobbyRowUI>());
            Wire(browserScreen, "refreshButton", refreshB);
            Wire(browserScreen, "codeInput", codeInput);
            Wire(browserScreen, "joinByCodeButton", codeJoinB);
            Wire(browserScreen, "statusLabel", browserStatus);
            Wire(browserScreen, "backButton", browserBack);

            SavePrefab(canvasGo, $"{UI_FOLDER}/MainMenuUI.prefab");
        }

        // ================= world UI =================

        private static void BuildWorldUiPrefab(GameObject settingsPanelPrefab, InputActionAsset actions)
        {
            var canvasGo = CanvasRoot("WorldUI");

            // --- HUD ---
            var hud = ChildRect(canvasGo.transform, "HUD");
            Stretch(hud);
            var crosshair = new GameObject("Crosshair", typeof(Image));
            crosshair.transform.SetParent(hud, false);
            var chImg = crosshair.GetComponent<Image>();
            chImg.color = new Color(0.9f, 0.9f, 0.85f, 0.7f);
            chImg.raycastTarget = false;
            crosshair.GetComponent<RectTransform>().sizeDelta = new Vector2(5, 5);
            var prompt = Text(hud, "InteractPrompt", "", 22, TextAlignmentOptions.Center);
            var promptRt = prompt.GetComponent<RectTransform>();
            promptRt.anchorMin = promptRt.anchorMax = new Vector2(0.5f, 0.32f);
            promptRt.sizeDelta = new Vector2(800, 40);
            prompt.raycastTarget = false;

            var hudCtrl = hud.gameObject.AddComponent<HUDController>();
            Wire(hudCtrl, "crosshair", crosshair);
            Wire(hudCtrl, "promptLabel", prompt);

            // --- Pause panel ---
            var (pausePanel, pauseCol) = Screen("PausePanel", 420, canvasGo.transform);
            Text(pauseCol, "Title", "PAUSED", 44, TextAlignmentOptions.Center);
            var (resumeB, _) = TextButton(pauseCol, "ResumeButton", "Resume", 0, 52);
            var (pSettingsB, _) = TextButton(pauseCol, "SettingsButton", "Settings", 0, 52);
            var (saveB, _) = TextButton(pauseCol, "SaveButton", "Save", 0, 52);
            var pauseStatus = Text(pauseCol, "Status", "", 16, TextAlignmentOptions.Center);
            pauseStatus.color = DimText;
            var (leaveB, _) = TextButton(pauseCol, "LeaveButton", "Leave to Menu", 0, 52);

            // --- Settings (nested prefab instance) ---
            var settingsInstance = (GameObject)PrefabUtility.InstantiatePrefab(settingsPanelPrefab, canvasGo.transform);
            Stretch(settingsInstance.GetComponent<RectTransform>());
            var settingsScreen = settingsInstance.GetComponent<SettingsScreen>();

            var pause = canvasGo.AddComponent<PauseMenu>();
            Wire(pause, "panelRoot", pausePanel);
            Wire(pause, "resumeButton", resumeB);
            Wire(pause, "settingsButton", pSettingsB);
            Wire(pause, "saveButton", saveB);
            Wire(pause, "statusLabel", pauseStatus);
            Wire(pause, "leaveButton", leaveB);
            Wire(pause, "settingsScreen", settingsScreen);
            Wire(pause, "inputActions", actions);

            pausePanel.SetActive(false);
            settingsInstance.SetActive(false);

            SavePrefab(canvasGo, $"{UI_FOLDER}/WorldUI.prefab");
        }

        // ================= scenes =================

        private static void UpdateMainMenuScene(InputActionAsset actions)
        {
            var scene = EditorSceneManager.OpenScene(MAINMENU_SCENE, OpenSceneMode.Additive);

            foreach (var root in scene.GetRootGameObjects())
                if (root.name == "DebugSessionMenu" || root.name == "MainMenuUI")
                    Object.DestroyImmediate(root);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{UI_FOLDER}/MainMenuUI.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            SceneManager.MoveGameObjectToScene(instance, scene);

            EnsureEventSystem(scene, actions);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, MAINMENU_SCENE);
            EditorSceneManager.CloseScene(scene, true);
        }

        private static void UpdateWorldScene(InputActionAsset actions)
        {
            var scene = EditorSceneManager.OpenScene(WORLD_SCENE, OpenSceneMode.Additive);

            foreach (var root in scene.GetRootGameObjects())
                if (root.name == "WorldUI")
                    Object.DestroyImmediate(root);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{UI_FOLDER}/WorldUI.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            SceneManager.MoveGameObjectToScene(instance, scene);

            EnsureEventSystem(scene, actions);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, WORLD_SCENE);
            EditorSceneManager.CloseScene(scene, true);
        }

        private static void EnsureEventSystem(Scene scene, InputActionAsset actions)
        {
            GameObject es = null;
            foreach (var root in scene.GetRootGameObjects())
                if (root.GetComponent<EventSystem>() != null) { es = root; break; }
            if (es == null)
            {
                es = new GameObject("EventSystem", typeof(EventSystem));
                SceneManager.MoveGameObjectToScene(es, scene);
            }
            var legacy = es.GetComponent<StandaloneInputModule>();
            if (legacy != null) Object.DestroyImmediate(legacy);
            var module = es.GetComponent<InputSystemUIInputModule>();
            if (module == null) module = es.AddComponent<InputSystemUIInputModule>();
            module.actionsAsset = actions;
        }

        // ================= UI helpers =================

        private static GameObject CanvasRoot(string name)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            return go;
        }

        /// <summary>
        /// Full-stretch dark screen with a centered fixed-width column layout.
        ///
        /// <paramref name="showcase"/> makes it see-through instead: no flat background, a
        /// gradient scrim down the left edge, and the column pinned left-of-centre. That is
        /// the title screen, which has a lit 3D harbour behind it.
        /// </summary>
        private static (GameObject root, Transform column) Screen(
            string name, float columnWidth, Transform parent = null, bool showcase = false)
        {
            var root = new GameObject(name, typeof(Image));
            if (parent != null) root.transform.SetParent(parent, false);
            var backdrop = root.GetComponent<Image>();
            Stretch(root.GetComponent<RectTransform>());

            if (showcase)
            {
                backdrop.color = Color.clear;
                // A fully transparent Image still swallows clicks, which would make every
                // button under it dead.
                backdrop.raycastTarget = false;
                BuildScrim(root.transform);
            }
            else
            {
                backdrop.color = ScreenBg;
            }

            var column = new GameObject("Column", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            column.transform.SetParent(root.transform, false);
            var rt = column.GetComponent<RectTransform>();
            if (showcase)
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f);
                rt.pivot = new Vector2(0f, 0.5f);
                rt.anchoredPosition = new Vector2(150f, 0f);
            }
            else
            {
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
            }
            rt.sizeDelta = new Vector2(columnWidth, 0);
            var layout = column.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 14;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            column.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return (root, column.transform);
        }

        /// <summary>
        /// Left-edge darkening so white text stays readable over moonlit water. UGUI cannot
        /// draw a gradient without a sprite, so one is generated: a wide, short alpha ramp
        /// stretched over the left half of the screen.
        /// </summary>
        private static void BuildScrim(Transform parent)
        {
            var go = new GameObject("Scrim", typeof(Image));
            go.transform.SetParent(parent, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0.58f, 1f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;

            var image = go.GetComponent<Image>();
            image.sprite = EnsureScrimSprite();
            image.type = Image.Type.Simple;
            image.color = new Color(0.02f, 0.02f, 0.03f, 0.94f);
            image.raycastTarget = false;
        }

        private static Sprite EnsureScrimSprite()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(SCRIM_PATH);
            if (existing != null) return existing;

            // 128x4: horizontal ramp only, so it costs nothing and stretches cleanly.
            const int width = 128;
            var tex = new Texture2D(width, 4, TextureFormat.RGBA32, false, linear: false);
            var pixels = new Color32[width * 4];
            for (int x = 0; x < width; x++)
            {
                // Opaque for the first third, then eased out - a linear fade reads as a
                // visible hard edge where it meets the scene.
                float t = Mathf.InverseLerp(0.34f, 1f, (x + 0.5f) / width);
                byte a = (byte)Mathf.RoundToInt((1f - Mathf.SmoothStep(0f, 1f, t)) * 255f);
                for (int y = 0; y < 4; y++) pixels[y * width + x] = new Color32(255, 255, 255, a);
            }
            tex.SetPixels32(pixels);
            tex.Apply();

            File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), SCRIM_PATH),
                               tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(SCRIM_PATH, ImportAssetOptions.ForceUpdate);

            var importer = (TextureImporter)AssetImporter.GetAtPath(SCRIM_PATH);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();

            return AssetDatabase.LoadAssetAtPath<Sprite>(SCRIM_PATH);
        }

        private static Transform Column(Transform parent, string name, float spacing)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(VerticalLayoutGroup));
            go.transform.SetParent(parent, false);
            var layout = go.GetComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return go.transform;
        }

        private static GameObject Row(Transform parent, string name, float height)
        {
            var go = new GameObject(name, typeof(Image), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            if (parent != null) go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            var layout = go.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 10;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.padding = new RectOffset(8, 8, 4, 4);
            go.GetComponent<LayoutElement>().preferredHeight = height;
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(660, height);
            return go;
        }

        private static TMP_Text Text(Transform parent, string name, string text, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (_font != null) tmp.font = _font;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = TextCol;
            tmp.alignment = align;
            return tmp;
        }

        private static (Button, TMP_Text) TextButton(Transform parent, string name, string label, float width, float height)
        {
            var go = new GameObject(name, typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = ControlBg;
            var le = go.GetComponent<LayoutElement>();
            le.preferredHeight = height;
            if (width > 0) { le.preferredWidth = width; le.flexibleWidth = 0; }

            var text = Text(go.transform, "Label", label, 22, TextAlignmentOptions.Center);
            Stretch(text.GetComponent<RectTransform>());

            var button = go.GetComponent<Button>();
            var colors = button.colors;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.2f, 1f);
            colors.pressedColor = new Color(0.8f, 0.9f, 0.8f, 1f);
            button.colors = colors;
            return (button, text);
        }

        private static Slider SliderControl(Transform parent, string name, float min, float max, bool whole)
        {
            var go = DefaultControls.CreateSlider(new DefaultControls.Resources());
            go.name = name;
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 28;

            var slider = go.GetComponent<Slider>();
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = whole;

            Tint(go, "Background", new Color(0.09f, 0.09f, 0.11f, 1f));
            Tint(go, "Fill", Accent);
            Tint(go, "Handle", TextCol);
            return slider;
        }

        private static Toggle LabeledToggle(Transform parent, string label, out Toggle toggle)
        {
            var row = Row(parent, label.Replace(" ", "") + "Row", 40).transform;
            var text = Text(row, "Label", label, 18, TextAlignmentOptions.MidlineLeft);
            Flex(text.gameObject, 1f);

            var go = DefaultControls.CreateToggle(new DefaultControls.Resources());
            go.name = "Toggle";
            go.transform.SetParent(row, false);
            var legacyLabel = go.transform.Find("Label");
            if (legacyLabel != null) Object.DestroyImmediate(legacyLabel.gameObject);
            Fixed(go, 40);
            Tint(go, "Background", ControlBg);
            Tint(go, "Checkmark", Accent);

            toggle = go.GetComponent<Toggle>();
            return toggle;
        }

        private static Transform LabeledDropdown(Transform parent, string label, out TMP_Dropdown dropdown)
        {
            var row = Row(parent, label.Replace(" ", "") + "Row", 46).transform;
            var text = Text(row, "Label", label, 18, TextAlignmentOptions.MidlineLeft);
            Flex(text.gameObject, 1f);

            var go = TMP_DefaultControls.CreateDropdown(new TMP_DefaultControls.Resources());
            go.name = "Dropdown";
            go.transform.SetParent(row, false);
            Fixed(go, 300);
            RestyleDeep(go);

            dropdown = go.GetComponent<TMP_Dropdown>();
            return row;
        }

        private static Transform LabeledInput(Transform parent, string label, out TMP_InputField input)
        {
            var row = Row(parent, label.Replace(" ", "") + "Row", 48).transform;
            var text = Text(row, "Label", label, 18, TextAlignmentOptions.MidlineLeft);
            Flex(text.gameObject, 1f);
            input = InputControl(row, "Input", "");
            Fixed(input.gameObject, 300);
            return row;
        }

        private static TMP_InputField InputControl(Transform parent, string name, string placeholder)
        {
            var go = TMP_DefaultControls.CreateInputField(new TMP_DefaultControls.Resources());
            go.name = name;
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 44;
            RestyleDeep(go);

            var input = go.GetComponent<TMP_InputField>();
            if (input.placeholder is TMP_Text ph)
            {
                ph.text = placeholder;
                ph.color = DimText;
            }
            return input;
        }

        /// <summary>Simple vertical scroll list; returns the content transform rows go into.</summary>
        private static GameObject ScrollList(Transform parent, string name, float height)
        {
            var go = new GameObject(name, typeof(Image), typeof(ScrollRect), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.05f, 0.05f, 0.06f, 1f);
            go.GetComponent<LayoutElement>().preferredHeight = height;

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(go.transform, false);
            Stretch(viewport.GetComponent<RectTransform>());

            var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            var crt = content.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 1);
            crt.anchorMax = new Vector2(1, 1);
            crt.pivot = new Vector2(0.5f, 1);
            crt.sizeDelta = new Vector2(0, 0);
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 6;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.padding = new RectOffset(4, 4, 4, 4);
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = go.GetComponent<ScrollRect>();
            scroll.viewport = viewport.GetComponent<RectTransform>();
            scroll.content = crt;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30;

            return content;
        }

        private static void Spacer(Transform parent, float height)
        {
            var go = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().preferredHeight = height;
        }

        private static RectTransform ChildRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void Flex(GameObject go, float flexibleWidth)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.flexibleWidth = flexibleWidth;
        }

        private static void Fixed(GameObject go, float width)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.flexibleWidth = 0;
        }

        private static void Tint(GameObject root, string childName, Color color)
        {
            foreach (var img in root.GetComponentsInChildren<Image>(true))
                if (img.name == childName) img.color = color;
        }

        /// <summary>Dark restyle for TMP default controls (dropdown/input templates ship white).</summary>
        private static void RestyleDeep(GameObject root)
        {
            foreach (var img in root.GetComponentsInChildren<Image>(true))
            {
                img.color = img.name switch
                {
                    "Item Background" => PanelBg,
                    "Item Checkmark" => Accent,
                    "Arrow" => DimText,
                    _ => ControlBg,
                };
            }
            foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true))
            {
                tmp.color = TextCol;
                tmp.fontSize = 18;
            }
        }

        private static void Wire(Component target, string field, Object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop == null)
            {
                Debug.LogError($"[MenuBuilder] {target.GetType().Name} has no serialized field '{field}'.");
                return;
            }
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject SavePrefab(GameObject root, string path)
        {
            // Single choke point for the typeface. Text() covers what this builder authors
            // itself, but dropdowns, input fields and scroll lists come from
            // DefaultControls with the TMP default font already baked in, and they only
            // pass through here.
            if (_font != null)
                foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true))
                    tmp.font = _font;

            var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return saved;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
