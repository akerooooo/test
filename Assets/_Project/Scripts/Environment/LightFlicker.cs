using UnityEngine;

namespace MimicGame.Environment
{
    /// <summary>Bozuk lamba gibi rastgele titreyen ışık.</summary>
    [RequireComponent(typeof(Light))]
    public class LightFlicker : MonoBehaviour
    {
        [Range(0f, 1f)] public float flickerChance = 0.05f;
        public float minIntensityFactor = 0.1f;

        Light lamp;
        float baseIntensity;

        void Awake()
        {
            lamp = GetComponent<Light>();
            baseIntensity = lamp.intensity;
        }

        void Update()
        {
            float factor = Random.value < flickerChance ? Random.Range(minIntensityFactor, 0.6f) : 1f;
            lamp.intensity = baseIntensity * factor;
        }
    }
}
