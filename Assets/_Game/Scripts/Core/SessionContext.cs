using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// Menu-to-gameplay handoff state: which save slot was picked and what config the
    /// GameSetup screen produced. Written by menu screens, read by the session manager
    /// and save system. Static (not a scene object) so it survives every scene load
    /// without DontDestroyOnLoad plumbing.
    /// </summary>
    public static class SessionContext
    {
        public static GameConfig Config { get; set; } = new GameConfig();

        /// <summary>Save slot index selected on the SaveSlots screen. -1 = none (joined someone else's game).</summary>
        public static int SelectedSaveSlot { get; set; } = -1;

        /// <summary>True when the selected slot already has data and should be loaded rather than created.</summary>
        public static bool IsContinue { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Config = new GameConfig();
            SelectedSaveSlot = -1;
            IsContinue = false;
        }
    }
}
