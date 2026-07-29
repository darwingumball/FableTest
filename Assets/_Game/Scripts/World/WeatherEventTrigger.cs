using Game.Core;
using Game.Net;
using Unity.Netcode;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// Drives weather from gameplay: when the named event fires on the
    /// <see cref="GameEventBus"/> (quest completion, entering a region, a script calling
    /// GameEventBus.Fire), the SERVER switches weather. Clients ignore the event so the
    /// change stays authoritative and replicates once.
    ///
    /// Drop one on any object in the world scene, or several for a scripted sequence.
    /// </summary>
    public class WeatherEventTrigger : MonoBehaviour
    {
        [Tooltip("Event id from GameEventBus, e.g. a quest's onCompleteEvents entry.")]
        [SerializeField] private string eventId = "";
        [SerializeField] private WeatherType weather = WeatherType.Storm;
        [SerializeField] private float transitionSeconds = 25f;
        [SerializeField, Range(0f, 2f)] private float intensity = 1f;
        [Tooltip("Fire only the first time the event is seen.")]
        [SerializeField] private bool once = true;

        private bool _fired;

        private void OnEnable() => GameEventBus.OnGameEvent += OnEvent;
        private void OnDisable() => GameEventBus.OnGameEvent -= OnEvent;

        private void OnEvent(string id)
        {
            if (string.IsNullOrEmpty(eventId) || id != eventId) return;
            if (once && _fired) return;

            // Server owns weather; every client will see the result replicate.
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (WeatherManager.Instance == null) return;

            _fired = true;
            WeatherManager.Instance.ServerSetWeather(weather, transitionSeconds, intensity);
        }
    }
}
