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
    ///
    /// Every hit along the ray is considered, not just the first. Triggers have to be included
    /// in the query because most interactables ARE triggers (the helm, the ladders, the crane
    /// console), and that means the nearest thing under the crosshair is regularly something
    /// with no interactable on it at all - HDRP's underwater volume bounds is a 1200 m trigger
    /// box whose top face sits just below deck height, so on a boat it intercepted the ray to
    /// everything below eye level. Taking only the first hit meant an item on the deck could
    /// not be picked up while it could still be dragged around by
    /// <see cref="PhysicsPickup"/>, which ignores triggers. Nothing logged; E just did nothing.
    /// </summary>
    public class InteractionSystem : MonoBehaviour
    {
        [SerializeField] private Camera playerCamera;
        [SerializeField] private float interactRange = 3.5f;
        [SerializeField] private LayerMask interactMask = ~0;

        // Generous for a 3.5 m ray. Non-alloc, because this runs every frame for every player.
        private readonly RaycastHit[] _hits = new RaycastHit[16];

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
            int count = Physics.RaycastNonAlloc(ray, _hits, interactRange, interactMask,
                QueryTriggerInteraction.Collide);

            // RaycastNonAlloc does not sort, so track the nearest match rather than taking the
            // first one that happens to come back.
            float nearest = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (_hits[i].distance >= nearest) continue;

                var interactable = _hits[i].collider.GetComponentInParent<IInteractable>();
                if (interactable == null) continue;

                // An interactable with no prompt is declining to be used right now (an item
                // with no ItemData, a ladder mid-teardown). Skip it and keep looking behind it.
                string candidate = interactable.GetPrompt(_player);
                if (string.IsNullOrEmpty(candidate)) continue;

                nearest = _hits[i].distance;
                Current = interactable;
                prompt = candidate;
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
