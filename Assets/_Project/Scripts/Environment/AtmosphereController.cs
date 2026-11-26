using MimicGame.Network;
using MimicGame.Player;
using UnityEngine;

namespace MimicGame.Environment
{
    /// <summary>
    /// Yerel oyuncu (ölüyse izlediği oyuncu) tesisin içindeyse sisi yoğunlaştırıp kararttır,
    /// dışındaysa dış değerlere döner (0.5 sn'de yumuşak geçiş). Sahnede tek örnek bulunur.
    /// </summary>
    public class AtmosphereController : MonoBehaviour
    {
        [Header("Dış")]
        public Color outsideFogColor = new Color(0.06f, 0.07f, 0.1f);
        public float outsideFogDensity = 0.025f;
        [Header("İç")]
        public Color insideFogColor = new Color(0.01f, 0.01f, 0.015f);
        public float insideFogDensity = 0.05f;
        [Range(0f, 1f)] public float insideAmbientFactor = 0.3f;
        public float transitionSeconds = 0.5f;

        float blend; // 0 = dışarı, 1 = içeri
        Color ambSky, ambEquator, ambGround;

        void Awake()
        {
            ambSky = RenderSettings.ambientSkyColor;
            ambEquator = RenderSettings.ambientEquatorColor;
            ambGround = RenderSettings.ambientGroundColor;
        }

        void OnDestroy()
        {
            blend = 0f;
            Apply();
        }

        void Update()
        {
            var subject = ResolveSubject();
            float target = subject != null && subject.IsInside ? 1f : 0f;
            blend = Mathf.MoveTowards(blend, target, Time.deltaTime / Mathf.Max(0.01f, transitionSeconds));
            Apply();
        }

        void Apply()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = Color.Lerp(outsideFogColor, insideFogColor, blend);
            RenderSettings.fogDensity = Mathf.Lerp(outsideFogDensity, insideFogDensity, blend);
            float k = Mathf.Lerp(1f, insideAmbientFactor, blend);
            RenderSettings.ambientSkyColor = ambSky * k;
            RenderSettings.ambientEquatorColor = ambEquator * k;
            RenderSettings.ambientGroundColor = ambGround * k;
        }

        // Yerel oyuncu; ölüyse gözlemlediği oyuncu
        static PlayerNetwork ResolveSubject()
        {
            var local = PlayerNetwork.Local;
            if (local == null) return null;
            var health = local.Health;
            if (health != null && health.Dead && health.spectator != null && health.spectator.HasTarget)
            {
                ulong id = health.spectator.WatchedClientId;
                var all = PlayerNetwork.All;
                for (int i = 0; i < all.Count; i++)
                    if (all[i] != null && all[i].OwnerClientId == id) return all[i];
            }
            return local;
        }
    }
}
