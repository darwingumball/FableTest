using System.Collections;
using System.Collections.Generic;
using Game.Quests;
using TMPro;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// Top-right popups when a quest is accepted, an objective ticks, or a quest
    /// completes. Toasts stack downward and fade out.
    /// </summary>
    public class QuestToastUI : MonoBehaviour
    {
        [SerializeField] private RectTransform container;
        [SerializeField] private TMP_Text toastPrefab;
        [SerializeField] private float holdSeconds = 4f;
        [SerializeField] private float fadeSeconds = 1f;
        [SerializeField] private int maxToasts = 4;

        private readonly List<TMP_Text> _live = new();

        private void OnEnable()
        {
            QuestManager.QuestAccepted += OnAccepted;
            QuestManager.QuestCompleted += OnCompleted;
        }

        private void OnDisable()
        {
            QuestManager.QuestAccepted -= OnAccepted;
            QuestManager.QuestCompleted -= OnCompleted;
        }

        private void OnAccepted(QuestData quest) => Show(
            $"<size=70%><color=#94A88F>NEW JOB</color></size>\n{quest.title}");

        private void OnCompleted(QuestData quest)
        {
            string reward = quest.rewardCash > 0 ? $"  <color=#94A88F>+${quest.rewardCash}</color>" : "";
            Show($"<size=70%><color=#94A88F>COMPLETE</color></size>\n{quest.title}{reward}");
        }

        /// <summary>Public so other systems (pickups, admin) can surface notices too.</summary>
        public void Show(string message)
        {
            if (toastPrefab == null || container == null) return;

            var toast = Instantiate(toastPrefab, container);
            toast.text = message;
            toast.gameObject.SetActive(true);
            _live.Add(toast);

            while (_live.Count > maxToasts)
            {
                var oldest = _live[0];
                _live.RemoveAt(0);
                if (oldest != null) Destroy(oldest.gameObject);
            }

            StartCoroutine(FadeOut(toast));
        }

        private IEnumerator FadeOut(TMP_Text toast)
        {
            yield return new WaitForSeconds(holdSeconds);
            float t = 0f;
            Color baseColor = toast.color;
            while (t < fadeSeconds && toast != null)
            {
                t += Time.deltaTime;
                toast.color = new Color(baseColor.r, baseColor.g, baseColor.b,
                    Mathf.Lerp(1f, 0f, t / fadeSeconds));
                yield return null;
            }
            if (toast != null)
            {
                _live.Remove(toast);
                Destroy(toast.gameObject);
            }
        }
    }
}
