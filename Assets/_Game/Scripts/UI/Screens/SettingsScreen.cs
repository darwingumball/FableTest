using System.Collections.Generic;
using Game.Core;
using Game.Net;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// Settings with Graphics / Audio / Controls tabs. Used both from the main menu
    /// (via MenuScreenManager) and the pause menu (standalone panel) - closing raises
    /// <see cref="OnCloseRequested"/> and the owner decides what to do.
    /// Changes apply immediately; everything persists on close.
    /// </summary>
    public class SettingsScreen : MenuScreen
    {
        [Header("Tabs")]
        [SerializeField] private Button graphicsTabButton;
        [SerializeField] private Button audioTabButton;
        [SerializeField] private Button controlsTabButton;
        [SerializeField] private GameObject graphicsPanel;
        [SerializeField] private GameObject audioPanel;
        [SerializeField] private GameObject controlsPanel;

        [Header("Graphics")]
        [SerializeField] private TMP_Dropdown qualityDropdown;
        [SerializeField] private TMP_Dropdown resolutionDropdown;
        [SerializeField] private TMP_Dropdown windowModeDropdown;
        [SerializeField] private Toggle vsyncToggle;
        [SerializeField] private Slider fovSlider;
        [SerializeField] private TMP_Text fovLabel;
        [SerializeField] private TMP_Dropdown psxResDropdown;
        [SerializeField] private TMP_Dropdown aaDropdown;
        [SerializeField] private Toggle motionBlurToggle;
        [SerializeField] private Toggle filmGrainToggle;

        [Header("Audio")]
        [SerializeField] private Slider masterSlider;
        [SerializeField] private Slider musicSlider;
        [SerializeField] private Slider sfxSlider;

        [Header("Controls")]
        [SerializeField] private Slider sensitivitySlider;
        [SerializeField] private TMP_Text sensitivityLabel;
        [SerializeField] private Toggle invertYToggle;
        [SerializeField] private Transform rebindContainer;
        [SerializeField] private RebindRow rebindRowPrefab;
        [SerializeField] private Button resetBindingsButton;
        [SerializeField] private InputActionAsset inputActions;

        [Header("Close")]
        [SerializeField] private Button backButton;

        public event System.Action OnCloseRequested;

        private static readonly int[] PsxHeights = { 240, 360, 480, 0 };
        private static readonly string[] RebindableActions =
            { "Jump", "Sprint", "Crouch", "Interact", "Grab", "Throw", "TabMenu", "Map", "Console", "Pause" };

        private List<Resolution> _resolutions = new();
        private bool _initialized;
        private bool _suppress;

        private void Awake()
        {
            graphicsTabButton.onClick.AddListener(() => ShowTab(graphicsPanel));
            audioTabButton.onClick.AddListener(() => ShowTab(audioPanel));
            controlsTabButton.onClick.AddListener(() => ShowTab(controlsPanel));
            backButton.onClick.AddListener(Close);

            WireGraphics();
            WireAudio();
            WireControls();
        }

        public override void OnShown()
        {
            RefreshFromData();
            ShowTab(graphicsPanel);
        }

        private void Close()
        {
            SettingsService.Save();
            OnCloseRequested?.Invoke();
        }

        private void ShowTab(GameObject panel)
        {
            graphicsPanel.SetActive(panel == graphicsPanel);
            audioPanel.SetActive(panel == audioPanel);
            controlsPanel.SetActive(panel == controlsPanel);
        }

        // ---------------- wiring ----------------

        private void WireGraphics()
        {
            qualityDropdown.onValueChanged.AddListener(v => Apply(d => d.qualityLevel = v));
            resolutionDropdown.onValueChanged.AddListener(v => Apply(d =>
            {
                if (v <= 0) { d.resWidth = 0; d.resHeight = 0; }
                else { d.resWidth = _resolutions[v - 1].width; d.resHeight = _resolutions[v - 1].height; }
            }));
            windowModeDropdown.onValueChanged.AddListener(v => Apply(d => d.windowMode = v));
            vsyncToggle.onValueChanged.AddListener(v => Apply(d => d.vsync = v ? 1 : 0));
            fovSlider.onValueChanged.AddListener(v =>
            {
                fovLabel.text = $"Field of view: {(int)v}";
                Apply(d => d.fov = v);
            });
            psxResDropdown.onValueChanged.AddListener(v => Apply(d => d.psxInternalHeight = PsxHeights[v]));
            aaDropdown.onValueChanged.AddListener(v => Apply(d => d.antiAliasing = v));
            motionBlurToggle.onValueChanged.AddListener(v => Apply(d => d.motionBlur = v));
            filmGrainToggle.onValueChanged.AddListener(v => Apply(d => d.filmGrain = v));
        }

        private void WireAudio()
        {
            masterSlider.onValueChanged.AddListener(v => Apply(d => d.masterVolume = v));
            musicSlider.onValueChanged.AddListener(v => Apply(d => d.musicVolume = v));
            sfxSlider.onValueChanged.AddListener(v => Apply(d => d.sfxVolume = v));
        }

        private void WireControls()
        {
            sensitivitySlider.onValueChanged.AddListener(v =>
            {
                sensitivityLabel.text = $"Mouse sensitivity: {v:0.00}";
                Apply(d => d.mouseSensitivity = v);
            });
            invertYToggle.onValueChanged.AddListener(v => Apply(d => d.invertY = v));
            resetBindingsButton.onClick.AddListener(() =>
            {
                inputActions.RemoveAllBindingOverrides();
                SettingsService.Data.bindingOverrides = "";
                SettingsService.Save();
                BuildRebindRows();
            });
        }

        private void Apply(System.Action<SettingsData> mutate)
        {
            if (_suppress) return;
            mutate(SettingsService.Data);
            SettingsService.ApplyGraphicsGlobal();
            SettingsService.ApplyAudio();
            if (NetworkPlayer.Local != null)
                SettingsService.ApplyToPlayer(NetworkPlayer.Local);
        }

        // ---------------- refresh ----------------

        private void RefreshFromData()
        {
            _suppress = true;
            var d = SettingsService.Data;

            if (!_initialized)
            {
                BuildStaticOptions();
                _initialized = true;
            }

            qualityDropdown.value = Mathf.Clamp(d.qualityLevel, 0, qualityDropdown.options.Count - 1);
            resolutionDropdown.value = FindResolutionIndex(d);
            windowModeDropdown.value = Mathf.Clamp(d.windowMode, 0, 2);
            vsyncToggle.isOn = d.vsync > 0;
            fovSlider.value = d.fov;
            fovLabel.text = $"Field of view: {(int)d.fov}";
            psxResDropdown.value = Mathf.Max(0, System.Array.IndexOf(PsxHeights, d.psxInternalHeight));
            aaDropdown.value = Mathf.Clamp(d.antiAliasing, 0, aaDropdown.options.Count - 1);
            motionBlurToggle.isOn = d.motionBlur;
            filmGrainToggle.isOn = d.filmGrain;

            masterSlider.value = d.masterVolume;
            musicSlider.value = d.musicVolume;
            sfxSlider.value = d.sfxVolume;

            sensitivitySlider.value = d.mouseSensitivity;
            sensitivityLabel.text = $"Mouse sensitivity: {d.mouseSensitivity:0.00}";
            invertYToggle.isOn = d.invertY;

            SettingsService.ApplyBindingOverrides(inputActions);
            BuildRebindRows();
            _suppress = false;
        }

        private void BuildStaticOptions()
        {
            qualityDropdown.ClearOptions();
            qualityDropdown.AddOptions(new List<string>(QualitySettings.names));

            _resolutions = new List<Resolution>();
            var seen = new HashSet<(int, int)>();
            foreach (var r in Screen.resolutions)
                if (seen.Add((r.width, r.height)))
                    _resolutions.Add(r);
            var resOptions = new List<string> { "Native" };
            foreach (var r in _resolutions) resOptions.Add($"{r.width} x {r.height}");
            resolutionDropdown.ClearOptions();
            resolutionDropdown.AddOptions(resOptions);

            windowModeDropdown.ClearOptions();
            windowModeDropdown.AddOptions(new List<string> { "Borderless", "Fullscreen", "Windowed" });

            psxResDropdown.ClearOptions();
            psxResDropdown.AddOptions(new List<string> { "240p (PSX)", "360p", "480p", "Native" });

            aaDropdown.ClearOptions();
            aaDropdown.AddOptions(new List<string> { "Off (PSX)", "FXAA", "TAA (smears PSX look)", "SMAA" });
        }

        private int FindResolutionIndex(SettingsData d)
        {
            if (d.resWidth <= 0) return 0;
            for (int i = 0; i < _resolutions.Count; i++)
                if (_resolutions[i].width == d.resWidth && _resolutions[i].height == d.resHeight)
                    return i + 1;
            return 0;
        }

        private void BuildRebindRows()
        {
            for (int i = rebindContainer.childCount - 1; i >= 0; i--)
                Destroy(rebindContainer.GetChild(i).gameObject);

            var map = inputActions.FindActionMap("Gameplay");
            if (map == null) return;
            foreach (var actionName in RebindableActions)
            {
                var action = map.FindAction(actionName);
                if (action == null) continue;
                var row = Instantiate(rebindRowPrefab, rebindContainer);
                row.Bind(action, () => SettingsService.StoreBindingOverrides(inputActions));
            }
        }
    }
}
