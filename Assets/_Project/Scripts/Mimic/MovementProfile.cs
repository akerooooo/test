using UnityEngine;

namespace MimicGame.Mimic
{
    /// <summary>Üstel sönümlü olay oranı (olay/dk). Az veri varken öncül (prior) değere yaslanır.</summary>
    public sealed class RateEma
    {
        const float Tau = 420f; // yaklaşık 7 dk hafıza
        public float events;
        public float secs;

        public void Tick(float dt)
        {
            float f = Mathf.Exp(-dt / Tau);
            events *= f;
            secs = secs * f + dt;
        }

        public void Add(float n = 1f) => events += n;

        /// <summary>Dakikadaki olay sayısı; öncül, yarım dakikalık sanal gözlem gibi karışır.</summary>
        public float PerMinute(float prior) => (events + prior * 0.5f) / (secs / 60f + 0.5f);
    }

    /// <summary>Bir oyuncunun hareket "parmak izi" (üstel hareketli ortalamalar ve dağılımlar).</summary>
    public class MovementProfile
    {
        // ---- Temel (eski) ----
        public float walkSpeed = 3.5f;
        public float sprintSpeed = 6f;
        public float sprintRatio = 0.15f;
        public float crouchRatio = 0.1f;
        public float stillRatio = 0.2f;
        public float avgStillDuration = 1.5f;
        public float flashlightRatio = 0.5f;
        /// <summary>Son ölçülen yatay hız (m/sn).</summary>
        public float currentSpeed;

        // ---- Zengin profil ----
        /// <summary>Bakış (yaw) dönme hızı ortalaması ve std. sapması (derece/sn, yalnızca dönerken).</summary>
        public float turnSpeedMean = 140f;
        public float turnSpeedStd = 70f;
        /// <summary>Dururken bakış titremesi (derece/sn).</summary>
        public float lookJitter = 6f;
        /// <summary>Dakikada geriye bakma sayısı (0.5 sn içinde > 120 derece dönüş).</summary>
        public float lookBehindPerMin = 0.6f;
        /// <summary>Dakikada zıplama sayısı.</summary>
        public float jumpPerMin = 0.5f;
        /// <summary>Yürürken eğilmiş olma oranı.</summary>
        public float crouchWalkRatio = 0.1f;
        /// <summary>Koşu atağı süresi ortalaması (sn).</summary>
        public float sprintBurstMean = 2.5f;
        /// <summary>Grupta en yakın oyuncuya tercih edilen mesafe (m).</summary>
        public float groupDistMean = 3.5f;
        /// <summary>Grupta konum: diğerinin önüne göre açı (0 = önünde, +-90 yan, 180 arkasında).</summary>
        public float groupAngleDeg;
        /// <summary>Grupta geçirilen örnek sayısı (veri güveni).</summary>
        public int groupSamples;
        /// <summary>Elde eşya tutma oranı.</summary>
        public float itemHoldRatio = 0.3f;
        /// <summary>Dakikada konuşma sayısı.</summary>
        public float speechPerMin = 3f;
        /// <summary>Kapıyı kapatma alışkanlığı (0-1).</summary>
        public float doorCloseRatio = 0.3f;
        /// <summary>Toplam örnek sayısı.</summary>
        public int sampleCount;

        // Dahili durum
        internal Vector3 lastPos;
        internal bool hasLast;
        internal float stillTimer;
        internal float turnVar = 70f * 70f;
        internal float groupVecX = 1f, groupVecY;
        internal readonly RateEma lookBehind = new RateEma();
        internal readonly RateEma jumps = new RateEma();
        internal readonly RateEma speech = new RateEma();

        /// <summary>Oyuncunun profili; veri yoksa varsayılanlarla yeni profil oluşturur.</summary>
        public static MovementProfile Get(ulong clientId) => BehaviorRecorder.GetProfile(clientId);
    }

    /// <summary>Eski ad: yalnızca profil erişimi (örnekleme artık BehaviorRecorder'da).</summary>
    public static class MovementProfiler
    {
        public static MovementProfile Get(ulong clientId) => BehaviorRecorder.GetProfile(clientId);
    }
}
