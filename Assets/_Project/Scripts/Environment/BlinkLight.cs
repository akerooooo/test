using UnityEngine;

namespace MimicGame.Environment
{
    /// <summary>Uçak ikaz ışığı gibi periyodik yanıp sönen ışık (varsayılan 1 sn periyot).</summary>
    [RequireComponent(typeof(Light))]
    public class BlinkLight : MonoBehaviour
    {
        public float period = 1f;
        [Range(0.05f, 1f)] public float onFraction = 0.3f;
        [Tooltip("İsteğe bağlı: ışıkla birlikte açılıp kapanan mercek görseli.")]
        public Renderer lens;

        Light lamp;
        float phase;

        void Awake()
        {
            lamp = GetComponent<Light>();
            phase = Random.value * period; // her ışık farklı fazda
        }

        void Update()
        {
            float t = Mathf.Repeat(Time.time + phase, Mathf.Max(0.05f, period));
            bool on = t < period * onFraction;
            if (lamp.enabled != on) lamp.enabled = on;
            if (lens != null && lens.enabled != on) lens.enabled = on;
        }
    }
}
