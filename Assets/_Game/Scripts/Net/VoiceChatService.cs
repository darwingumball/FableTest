using System;
using System.Threading.Tasks;
using Game.Core;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Vivox;
using UnityEngine;

namespace Game.Net
{
    /// <summary>
    /// Vivox voice chat: a positional channel everyone is always in, plus an optional
    /// server-wide channel you can toggle on.
    ///
    /// Voice is deliberately NOT routed through Netcode. Vivox carries its own audio over
    /// its own servers, so the only thing the game has to do is agree on a channel name and
    /// keep telling Vivox where the listener is. That also means voice survives a Netcode
    /// hiccup, and that a player who has not spawned yet can already hear the lobby.
    ///
    /// Channel names are derived from the session, not hardcoded: two sessions running
    /// against the same Vivox project must not be able to hear each other.
    /// </summary>
    public class VoiceChatService : MonoBehaviour
    {
        public static VoiceChatService Instance { get; private set; }

        [Header("Proximity")]
        [Tooltip("Metres beyond which a speaker is inaudible.")]
        [SerializeField] private int audibleDistance = 28;
        [Tooltip("Metres within which a speaker is at full volume. Beyond this, volume " +
                 "falls off toward audibleDistance.")]
        [SerializeField] private int conversationalDistance = 4;
        [Tooltip("1 = natural rolloff. Lower carries further, higher drops off sharply.")]
        [SerializeField] private float fadeIntensity = 1f;

        [Header("Input")]
        [Tooltip("Push to talk. When off, the mic is open unless self-muted.")]
        [SerializeField] private bool pushToTalk = true;

        /// <summary>Raised when the local player's transmit state changes (HUD indicator).</summary>
        public event Action<bool> OnTransmittingChanged;
        /// <summary>Raised when the global channel is joined or left.</summary>
        public event Action<bool> OnGlobalVoiceChanged;

        public bool IsReady { get; private set; }
        public bool IsTransmitting { get; private set; }
        public bool SelfMuted { get; private set; }
        public bool GlobalVoiceEnabled { get; private set; }
        public bool PushToTalk => pushToTalk;

        private string _positionalChannel;
        private string _globalChannel;
        private Transform _listener;
        private bool _loggedIn;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        /// <summary>
        /// Vivox, or null when the SDK is not actually up.
        ///
        /// <see cref="IsReady"/> cannot be trusted on its own. This component is
        /// DontDestroyOnLoad, so it outlives the session that started it: leaving a game
        /// tears UGS down and nulls <c>VivoxService.Instance</c> while IsReady is still true
        /// from the session before. That combination made Update throw a
        /// NullReferenceException every single frame, forever, with nothing in the message
        /// to say voice had simply gone away.
        ///
        /// The state check comes first because reading Instance before UGS is initialised
        /// is not guaranteed to merely return null.
        /// </summary>
        private static IVivoxService Vivox
        {
            get
            {
                try
                {
                    return UnityServices.State == ServicesInitializationState.Initialized
                        ? VivoxService.Instance
                        : null;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Called when Vivox disappears underneath us. Drops back to a clean not-running
        /// state so a later session starts voice from scratch instead of assuming it is
        /// still logged in.
        /// </summary>
        private void HandleVoiceLost()
        {
            Debug.LogWarning("[Voice] Vivox is no longer available (session ended or UGS shut " +
                             "down). Voice stopped; it will restart with the next session.");
            IsReady = false;
            GlobalVoiceEnabled = false;
            IsTransmitting = false;
            _positionalChannel = null;
            _globalChannel = null;
            _loggedIn = false;
            _listener = null;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Brings voice up as soon as the world exists. This lives in the World scene
        /// rather than hooking OnSessionStarted because that event fires BEFORE the scene
        /// load - a listener in here would never hear it.
        /// </summary>
        private async void Start()
        {
            var session = NetworkSessionManager.Instance;
            if (session == null) return;

            string key = session.SessionKey;
            if (string.IsNullOrEmpty(key)) return;

            string name = ServerPlayerRegistry.TryGet(
                Unity.Netcode.NetworkManager.Singleton != null
                    ? Unity.Netcode.NetworkManager.Singleton.LocalClientId : 0,
                out var entry) ? entry.DisplayName : null;

            await StartSessionVoiceAsync(key, name);
        }

        /// <summary>
        /// Brings voice up for a session. Safe to call again; a second call for the same
        /// session is ignored rather than rejoining and cutting everyone off.
        /// </summary>
        public async Task StartSessionVoiceAsync(string sessionKey, string displayName)
        {
            if (string.IsNullOrEmpty(sessionKey))
            {
                Debug.LogWarning("[Voice] No session key; voice not started.");
                return;
            }

            // Vivox channel names allow a restricted character set, and a raw join code or
            // lobby id can contain anything. Hashing keeps it legal and still unique.
            string key = Mathf.Abs(sessionKey.GetHashCode()).ToString();
            string positional = "prox-" + key;
            if (IsReady && _positionalChannel == positional) return;

            if (Vivox == null)
            {
                Debug.LogWarning("[Voice] UGS is not initialised, so Vivox is unavailable. " +
                                 "Voice is disabled for this session.");
                IsReady = false;
                return;
            }

            try
            {
                if (!_loggedIn)
                {
                    await VivoxService.Instance.InitializeAsync();
                    await VivoxService.Instance.LoginAsync(new LoginOptions
                    {
                        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Player" : displayName,
                        PlayerId = AuthenticationService.Instance.PlayerId,
                        EnableTTS = false,
                    });
                    _loggedIn = true;
                    VivoxService.Instance.ChannelMessageReceived += HandleChannelMessage;
                }

                _positionalChannel = positional;
                _globalChannel = "all-" + key;

                await VivoxService.Instance.JoinPositionalChannelAsync(
                    _positionalChannel, ChatCapability.AudioOnly,
                    new Channel3DProperties(audibleDistance, conversationalDistance,
                        fadeIntensity, AudioFadeModel.InverseByDistance));

                IsReady = true;
                // Start closed under push-to-talk so nobody's first act is broadcasting
                // their room to the lobby.
                ApplyTransmitState(false);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Voice] Failed to start: {e.Message}");
                IsReady = false;
            }
        }

        public async Task StopVoiceAsync()
        {
            if (!_loggedIn) return;
            var vivox = Vivox;
            if (vivox == null) { HandleVoiceLost(); return; }
            try { await vivox.LeaveAllChannelsAsync(); }
            catch (Exception e) { Debug.LogWarning($"[Voice] Leave failed: {e.Message}"); }
            IsReady = false;
            GlobalVoiceEnabled = false;
            _positionalChannel = null;
        }

        /// <summary>Joins or leaves the server-wide channel. Proximity is unaffected.</summary>
        public async Task SetGlobalVoiceAsync(bool enabled)
        {
            if (!IsReady || string.IsNullOrEmpty(_globalChannel)) return;
            if (GlobalVoiceEnabled == enabled) return;
            var vivox = Vivox;
            if (vivox == null) { HandleVoiceLost(); return; }

            try
            {
                if (enabled)
                    await vivox.JoinGroupChannelAsync(
                        _globalChannel, ChatCapability.TextAndAudio);
                else
                    await vivox.LeaveChannelAsync(_globalChannel);

                GlobalVoiceEnabled = enabled;
                OnGlobalVoiceChanged?.Invoke(enabled);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Voice] Global channel toggle failed: {e.Message}");
            }
        }

        /// <summary>The transform Vivox treats as the listener - normally the player camera.</summary>
        public void SetListener(Transform listener) => _listener = listener;

        public void SetSelfMuted(bool muted)
        {
            SelfMuted = muted;
            ApplyTransmitState(IsTransmitting);
        }

        public void SetPushToTalk(bool enabled)
        {
            pushToTalk = enabled;
            ApplyTransmitState(enabled ? false : true);
        }

        /// <summary>Called every frame by the input layer with the talk key's state.</summary>
        public void SetTalkKeyHeld(bool held)
        {
            if (!pushToTalk) return;
            if (IsTransmitting != held) ApplyTransmitState(held);
        }

        private void ApplyTransmitState(bool wantTransmit)
        {
            if (!IsReady) return;

            var vivox = Vivox;
            if (vivox == null) { HandleVoiceLost(); return; }

            bool transmit = wantTransmit && !SelfMuted;
            if (transmit) vivox.UnmuteInputDevice();
            else vivox.MuteInputDevice();

            if (IsTransmitting == transmit) return;
            IsTransmitting = transmit;
            OnTransmittingChanged?.Invoke(transmit);
        }

        private void Update()
        {
            if (!IsReady || string.IsNullOrEmpty(_positionalChannel)) return;

            var vivox = Vivox;
            if (vivox == null)
            {
                HandleVoiceLost();
                return;
            }

            // The player spawns after this component starts, so the listener is picked up
            // lazily rather than wired in the editor.
            if (_listener == null)
            {
                var player = NetworkPlayer.Local;
                if (player == null) return;
                var cam = player.GetComponentInChildren<Camera>();
                _listener = cam != null ? cam.transform : player.transform;
            }

            // Vivox needs the listener pose every frame or voices stay pinned where the
            // player was when they joined. Passing the camera (not the body) matters:
            // panning is computed from the forward vector, so using the body would put
            // sound behind you whenever you looked over your shoulder.
            vivox.Set3DPosition(_listener.gameObject, _positionalChannel, allowPanning: true);
        }

        private static void HandleChannelMessage(VivoxMessage message)
        {
            // Voice channels can also carry text. Route it into the game's own chat so
            // there is one place messages appear, rather than a second parallel log.
            if (message.FromSelf) return;
            ChatRelay.ReceiveExternal(message.SenderDisplayName, message.MessageText);
        }
    }
}
