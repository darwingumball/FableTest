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

    /// <summary>
    /// Opt-in continuous variant for verbs that need to keep happening for as long as the
    /// button stays down - refuelling, later maybe a hand-crank or a hold-to-open door - rather
    /// than a single press toggling a state that then has to be validated open on trust.
    ///
    /// <c>InteractionSystem</c> calls <see cref="InteractHeld"/> every frame the Interact
    /// button is held AND this is the interactable currently under the crosshair, and
    /// <see cref="InteractReleased"/> the one frame either of those stops being true - button
    /// let go, looked away, walked out of range. That single call site is what makes this
    /// robust: the verb ends the instant any of those actually happens, with no distance
    /// check or timeout needed to guess whether the player is "still doing it".
    /// </summary>
    public interface IHoldInteractable : IInteractable
    {
        void InteractHeld(Net.NetworkPlayer player);
        void InteractReleased(Net.NetworkPlayer player);
    }
}
