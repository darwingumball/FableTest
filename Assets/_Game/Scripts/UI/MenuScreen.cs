using UnityEngine;

namespace Game.UI
{
    /// <summary>A full-screen menu panel managed by <see cref="MenuScreenManager"/>.</summary>
    public abstract class MenuScreen : MonoBehaviour
    {
        public virtual void OnShown() { }
        public virtual void OnHidden() { }
    }
}
