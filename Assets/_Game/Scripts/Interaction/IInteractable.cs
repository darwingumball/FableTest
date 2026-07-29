namespace Game.Interaction
{
    /// <summary>
    /// Something the player can point at and press Interact on. Implementors decide
    /// whether the action runs locally or routes to the server via RPC.
    /// </summary>
    public interface IInteractable
    {
        /// <summary>HUD prompt, e.g. "Open door". Null/empty = not currently interactable.</summary>
        string GetPrompt(Net.NetworkPlayer player);

        void Interact(Net.NetworkPlayer player);
    }
}
