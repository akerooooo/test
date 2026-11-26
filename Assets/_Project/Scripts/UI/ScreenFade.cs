using System.Collections;
using UnityEngine;

namespace MimicGame.UI
{
    /// <summary>Kısa siyah kararma (ışınlanma geçişi). İlk kullanımda kendini oluşturur.</summary>
    public class ScreenFade : MonoBehaviour
    {
        static ScreenFade instance;
        float alpha;

        /// <summary>Ekranı karartır, tam karardığında atBlack'i çağırır, sonra açar. Toplam süre ≈ seconds.</summary>
        public static void Run(float seconds, System.Action atBlack)
        {
            if (instance == null)
            {
                var go = new GameObject("Ekran Kararması");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<ScreenFade>();
            }
            instance.StopAllCoroutines();
            instance.StartCoroutine(instance.Routine(Mathf.Max(0.05f, seconds), atBlack));
        }

        IEnumerator Routine(float seconds, System.Action atBlack)
        {
            float inT = seconds * 0.4f, outT = seconds * 0.6f;
            for (float t = 0f; t < inT; t += Time.unscaledDeltaTime)
            {
                alpha = t / inT;
                yield return null;
            }
            alpha = 1f;
            atBlack?.Invoke();
            yield return null;
            for (float t = 0f; t < outT; t += Time.unscaledDeltaTime)
            {
                alpha = 1f - t / outT;
                yield return null;
            }
            alpha = 0f;
        }

        void OnGUI()
        {
            if (alpha <= 0.001f) return;
            GUI.depth = -1000; // her şeyin üstünde
            GUI.color = new Color(0f, 0f, 0f, alpha);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
