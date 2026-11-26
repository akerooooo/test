using UnityEngine;

namespace MimicGame.Player
{
    /// <summary>F ile açılıp kapanan, pili biten el feneri. Pil azalınca titrer.</summary>
    [RequireComponent(typeof(PlayerInputReader))]
    public class Flashlight : MonoBehaviour
    {
        public Light spot;
        public float maxBattery = 100f;
        [Tooltip("Açıkken saniyede harcanan pil yüzdesi.")]
        public float drainPerSecond = 0.5f;

        public float Battery01 => battery / maxBattery;
        public bool IsOn => spot != null && spot.enabled;

        PlayerInputReader input;
        float battery;
        float baseIntensity;

        void Awake()
        {
            input = GetComponent<PlayerInputReader>();
            battery = maxBattery;
            if (spot != null)
            {
                baseIntensity = spot.intensity;
                spot.enabled = false;
            }
        }

        void Update()
        {
            if (spot == null) return;

            if (input.FlashlightPressed)
            {
                if (spot.enabled) spot.enabled = false;
                else if (battery > 0f) spot.enabled = true;
            }

            if (!spot.enabled) return;

            battery -= drainPerSecond * Time.deltaTime;
            if (battery <= 0f)
            {
                battery = 0f;
                spot.enabled = false;
                return;
            }

            bool low = Battery01 < 0.15f;
            spot.intensity = low && Random.value < 0.08f ? baseIntensity * 0.2f : baseIntensity;
        }

        public void Recharge() => battery = maxBattery;
    }
}
