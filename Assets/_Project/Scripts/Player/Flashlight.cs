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
        public float drainPerSecond = 0.28f;

        [Header("Işık hüzmesi (Awake'te spot'a uygulanır)")]
        public float beamRange = 30f;
        public float beamAngle = 62f;
        public float beamInnerAngle = 30f;
        public float beamIntensity = 9f;
        public Color beamColor = new Color(1f, 0.96f, 0.88f);

        /// <summary>Bu istemcinin kendi feneri (yoksa null).</summary>
        public static Flashlight Local { get; private set; }

        public float Battery01 => battery / maxBattery;
        public bool IsOn => isOn.Value;
        public bool IsFull => battery >= maxBattery - 0.01f;

        readonly NetworkVariable<bool> isOn = new NetworkVariable<bool>(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        PlayerInputReader input;
        PlayerHealth health;
        float battery;
        float baseIntensity;

        void Awake()
        {
            input = GetComponent<PlayerInputReader>();
            health = GetComponent<PlayerHealth>();
            battery = maxBattery;
            if (spot != null)
            {
                spot.type = LightType.Spot;
                spot.range = beamRange;
                spot.spotAngle = beamAngle;
                spot.innerSpotAngle = beamInnerAngle;
                spot.intensity = beamIntensity;
                spot.color = beamColor;
                baseIntensity = spot.intensity;
                spot.enabled = false;
            }
        }

        public override void OnNetworkSpawn() { if (IsOwner) Local = this; }
        public override void OnNetworkDespawn() { if (Local == this) Local = null; }

        void Update()
        {
            if (spot == null) return;

            // Ölü oyuncunun feneri kapalı: sahip değeri sıfırlar, herkes ışığı söndürür
            if (health != null && health.Dead)
            {
                if (IsOwner && IsSpawned && isOn.Value) isOn.Value = false;
                spot.enabled = false;
                return;
            }

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

            // Pil %20 altında ışık giderek kısılır (%0'da %40); %10 altında titrer. Yalnızca sahibinde yerel
            float b = IsOwner ? Battery01 : 1f;
            float dim = b < 0.2f ? Mathf.Lerp(0.4f, 1f, b / 0.2f) : 1f;
            bool flicker = IsOwner && b < 0.1f && Random.value < 0.08f;
            spot.intensity = baseIntensity * dim * (flicker ? 0.2f : 1f);
        }

        public void Recharge() => battery = maxBattery;

        /// <summary>Pili verilen oranda (0-1) doldurur.</summary>
        public void Charge(float amount01) =>
            battery = Mathf.Min(maxBattery, battery + amount01 * maxBattery);
    }
}
