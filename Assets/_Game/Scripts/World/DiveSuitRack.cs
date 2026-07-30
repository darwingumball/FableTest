using Game.Interaction;
using Game.Net;
using UnityEngine;

namespace Game.World
{
    /// <summary>
    /// The suit-up point on a <see cref="DiveRig"/> - a separate physical spot from the winch
    /// controls, the same way the crane's console is a separate object from the crane itself.
    /// A raycast has to resolve to ONE <see cref="IInteractable"/> per collider via
    /// GetComponentInParent, and "start diving" and "operate the winch" are different verbs at
    /// different places on the rig, so they cannot both live on <see cref="DiveRig"/> itself.
    /// This just forwards to it - same shape as <see cref="ElevatorButtonInteractable"/>.
    /// </summary>
    public class DiveSuitRack : MonoBehaviour, IInteractable
    {
        [SerializeField] private DiveRig rig;

        public string GetPrompt(NetworkPlayer player) => rig != null ? rig.GetSuitPrompt(player) : null;

        public void Interact(NetworkPlayer player) => rig?.SuitInteract(player);
    }
}
