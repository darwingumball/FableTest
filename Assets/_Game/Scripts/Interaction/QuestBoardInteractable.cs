using System.Linq;
using Game.Net;
using Game.Quests;
using UnityEngine;

namespace Game.Interaction
{
    /// <summary>
    /// Placeholder quest giver: interacting accepts the next available quest from the
    /// catalog. Real NPCs/dialogue replace this later; the accept/sync flow is identical.
    /// </summary>
    public class QuestBoardInteractable : MonoBehaviour, IInteractable
    {
        public string GetPrompt(NetworkPlayer player)
        {
            var next = NextAvailable();
            return next != null ? $"Accept job: {next.title}" : "Notice board (no new jobs)";
        }

        public void Interact(NetworkPlayer player)
        {
            var next = NextAvailable();
            if (next != null) QuestManager.Accept(next.Id);
        }

        private static QuestData NextAvailable() =>
            QuestManager.Catalog
                .Where(QuestManager.IsAvailable)
                .OrderByDescending(q => q.availableFromStart)
                .FirstOrDefault();
    }
}
