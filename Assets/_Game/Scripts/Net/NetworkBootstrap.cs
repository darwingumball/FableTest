using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Services.Core;
using Unity.Services.Authentication;

namespace Game.Net
{
    /// <summary>
    /// Lives in the Boot scene. Initializes Unity Gaming Services + anonymous auth once,
    /// then loads the MainMenu. <see cref="EnsureInitializedAsync"/> is idempotent and can
    /// be awaited from anywhere before touching Lobby/Relay APIs.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class NetworkBootstrap : MonoBehaviour
    {
        [Tooltip("Scene loaded once services + auth are ready.")]
        public string mainMenuSceneName = "MainMenu";

        [Tooltip("Disable Burst JIT at startup. This dev machine has a broken Burst install " +
                 "(UnityTransport 'Burst failed to compile Void Receive' on StartHost); the " +
                 "managed fallback is slower but works.")]
        public bool disableBurstFallback = true;

        public static bool ServicesReady { get; private set; }
        public static string LocalPlayerId { get; private set; }
        public static string LastInitError { get; private set; }

        private static Task _initTask;
        private static bool _hasInstance;
        private static bool _burstDisabled;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            ServicesReady = false;
            LocalPlayerId = null;
            LastInitError = null;
            _initTask = null;
            _hasInstance = false;
            // _burstDisabled intentionally NOT reset - Burst state is process-wide.
        }

        private async void Awake()
        {
            if (!Application.isPlaying) return;

            if (_hasInstance)
            {
                Destroy(gameObject);
                return;
            }
            _hasInstance = true;
            DontDestroyOnLoad(gameObject);

            if (disableBurstFallback && !_burstDisabled)
                DisableBurst();

            try
            {
                await EnsureInitializedAsync();
            }
            catch (Exception ex)
            {
                LastInitError = ex.Message;
                Debug.LogError($"[NetworkBootstrap] UGS init failed: {ex.Message}\n" +
                    "If this is an auth error: Unity Cloud Dashboard -> Player Authentication -> enable Anonymous sign-in.");
            }

            if (!string.IsNullOrEmpty(mainMenuSceneName)
                && SceneManager.GetActiveScene().name != mainMenuSceneName
                && Application.CanStreamedLevelBeLoaded(mainMenuSceneName))
            {
                SceneManager.LoadScene(mainMenuSceneName);
            }
        }

        private static void DisableBurst()
        {
            try
            {
                Unity.Burst.BurstCompiler.Options.EnableBurstCompilation = false;

                // In the editor the public flag alone does not stop FunctionPointer JIT;
                // Burst's internal ForceDisableBurstCompilation is the only switch that
                // gates the managed-delegate fallback. Reflection is the only way in.
                var t = typeof(Unity.Burst.BurstCompiler).Assembly.GetType("Unity.Burst.BurstCompilerOptions");
                var field = t?.GetField("ForceDisableBurstCompilation",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                field?.SetValue(null, true);

                _burstDisabled = true;
                Debug.LogWarning("[NetworkBootstrap] Burst compilation disabled (dev fallback for broken Burst install).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetworkBootstrap] Could not disable Burst: {ex.Message}");
            }
        }

        /// <summary>Initializes UGS + anonymous auth. Idempotent; await before any Lobby/Relay call.</summary>
        public static Task EnsureInitializedAsync()
        {
            if (ServicesReady || !Application.isPlaying) return Task.CompletedTask;
            _initTask ??= DoInitAsync();
            return _initTask;
        }

        private static async Task DoInitAsync()
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
                await UnityServices.InitializeAsync();

            var auth = AuthenticationService.Instance;
            if (!auth.IsSignedIn)
            {
                try
                {
                    await auth.SignInAnonymouslyAsync();
                }
                catch (AuthenticationException aex)
                {
                    LastInitError = aex.Message;
                    throw new Exception(
                        "Anonymous sign-in failed. Enable Player Authentication + Anonymous Sign-In " +
                        $"in the Unity Cloud dashboard for this project. Underlying: {aex.Message}", aex);
                }
                catch (RequestFailedException rex)
                {
                    LastInitError = rex.Message;
                    throw new Exception(
                        "UGS request failed during sign-in. Check connectivity and that the project is " +
                        $"linked to a Unity Cloud Project ID. Underlying: {rex.Message}", rex);
                }
            }

            LocalPlayerId = auth.PlayerId;
            ServicesReady = true;
            Debug.Log($"[NetworkBootstrap] UGS ready. PlayerId={LocalPlayerId}");
        }
    }
}
