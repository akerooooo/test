using Unity.Netcode;
using UnityEngine;

namespace MimicGame.Player
{
    /// <summary>F ile açılıp kapanan, pili biten el feneri. Pil azalınca titrer. Açık/kapalı durumu ağda senkron.</summary>
    [RequireComponent(typeof(PlayerInputReader))]
    public class Flashlight : NetworkBehaviour
    {
        public Light spot;
        public float maxBattery = 100f;
        [Tooltip("Açıkken saniyede harcanan pil yüzdesi.")]
        public float drainPerSecond = 0.5f;

        public float Battery01 => battery / maxBattery;
        public bool IsOn => isOn.Value;

        readonly NetworkVariable<bool> isOn = new NetworkVariable<bool>(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

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

            if (IsOwner)
            {
                if (input.FlashlightPressed)
                {
                    if (isOn.Value) isOn.Value = false;
                    else if (battery > 0f) isOn.Value = true;
                }

                if (isOn.Value)
                {
                    battery -= drainPerSecond * Time.deltaTime;
                    if (battery <= 0f)
                    {
                        battery = 0f;
                        isOn.Value = false;
                    }
                }
            }

            spot.enabled = isOn.Value;
            if (!spot.enabled) return;

            // Düşük pil titremesi yalnızca sahibinde yerel
            bool low = IsOwner && Battery01 < 0.15f;
            spot.intensity = low && Random.value < 0.08f ? baseIntensity * 0.2f : baseIntensity;
        }

        public void Recharge() => battery = maxBattery;
    }
}
