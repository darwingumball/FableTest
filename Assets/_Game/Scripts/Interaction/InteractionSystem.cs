using System;
using Game.Net;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Interaction
{
    /// <summary>
    /// Raycasts from the player camera for <see cref="IInteractable"/>s and fires Interact
    /// on keypress. Runs only on the owning client (NetworkPlayer enables it).
    /// The HUD subscribes to <see cref="OnPromptChanged"/> - no direct UI references here.
    /// </summary>
    public class InteractionSystem : MonoBehaviour
    {
        [SerializeField] private Camera playerCamera;
        [SerializeField] private float interactRange = 3.5f;
        [SerializeField] private LayerMask interactMask = ~0;

        /// <summary>Null when nothing interactable is under the crosshair.</summary>
        public event Action<string> OnPromptChanged;

        public IInteractable Current { get; private set; }

        private NetworkPlayer _player;
        private InputAction _interactAction;
        private string _lastPrompt;

        private void Awake()
        {
            _player = GetComponentInParent<NetworkPlayer>();
        }

        private void Start()
        {
            var fpc = GetComponentInParent<Game.Player.FirstPersonController>();
            if (fpc != null && fpc.InputActions != null)
                _interactAction = fpc.InputActions.FindActionMap("Gameplay")?.FindAction("Interact");
        }

        private void Update()
        {
            if (playerCamera == null) return;

            Current = null;
            string prompt = null;

            var ray = new Ray(playerCamera.transform.position, playerCamera.transform.forward);
            if (Physics.Raycast(ray, out var hit, interactRange, interactMask, QueryTriggerInteraction.Collide))
            {
                var interactable = hit.collider.GetComponentInParent<IInteractable>();
                if (interactable != null)
                {
                    prompt = interactable.GetPrompt(_player);
                    if (!string.IsNullOrEmpty(prompt))
                        Current = interactable;
                }
            }

            if (prompt != _lastPrompt)
            {
                _lastPrompt = prompt;
                OnPromptChanged?.Invoke(prompt);
            }

            if (Current != null && _interactAction != null && _interactAction.WasPressedThisFrame())
                Current.Interact(_player);
        }

        public void SetCamera(Camera cam) => playerCamera = cam;
    }
}
