using Game.Net;
using Game.World;
using UnityEngine;

namespace Game.Interaction
{
    /// <summary>Call button: sends the linked elevator to its next floor.</summary>
    public class ElevatorButtonInteractable : MonoBehaviour, IInteractable
    {
        [SerializeField] private ElevatorPlatform elevator;

        public string GetPrompt(NetworkPlayer player) =>
            elevator != null ? "Call elevator" : null;

        public void Interact(NetworkPlayer player)
        {
            if (elevator != null) elevator.RequestNextFloor();
        }
    }
}
