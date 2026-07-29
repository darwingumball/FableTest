using System.Text;
using Game.Quests;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    /// <summary>Quests tab: renders active/completed quests into one text block.</summary>
    public class QuestLogUI : MonoBehaviour
    {
        [SerializeField] private TMP_Text logText;

        private void OnEnable()
        {
            QuestManager.Changed += Rebuild;
            Rebuild();
        }

        private void OnDisable() => QuestManager.Changed -= Rebuild;

        private void Rebuild()
        {
            var sb = new StringBuilder();

            if (QuestManager.Active.Count == 0 && QuestManager.CompletedQuests.Count == 0)
            {
                sb.Append("<color=#8C8C85>No active quests. Find work out there.</color>");
            }

            foreach (var state in QuestManager.Active.Values)
            {
                var quest = QuestManager.Get(state.questId);
                if (quest == null) continue;
                sb.Append("<b>").Append(quest.title).Append("</b>");
                if (quest.sharedProgress) sb.Append("  <color=#94A88F><size=80%>[shared]</size></color>");
                sb.AppendLine();
                if (!string.IsNullOrEmpty(quest.description))
                    sb.Append("<color=#8C8C85><i>").Append(quest.description).AppendLine("</i></color>");
                for (int i = 0; i < quest.objectives.Length; i++)
                {
                    var obj = quest.objectives[i];
                    bool done = state.progress[i] >= obj.requiredAmount;
                    sb.Append(done ? "<color=#94A88F>  + " : "<color=#D8D8D0>  - ")
                      .Append(obj.description)
                      .Append("  (").Append(state.progress[i]).Append('/').Append(obj.requiredAmount).Append(')')
                      .AppendLine("</color>");
                }
                sb.AppendLine();
            }

            if (QuestManager.CompletedQuests.Count > 0)
            {
                sb.AppendLine("<color=#8C8C85>Completed:</color>");
                foreach (var id in QuestManager.CompletedQuests)
                {
                    var quest = QuestManager.Get(id);
                    sb.Append("<color=#6E7A69>  + ").Append(quest != null ? quest.title : id).AppendLine("</color>");
                }
            }

            logText.text = sb.ToString();
        }
    }
}
