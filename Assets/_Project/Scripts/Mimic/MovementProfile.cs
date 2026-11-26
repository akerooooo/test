using System.Collections.Generic;
using MimicGame.Network;
using MimicGame.Player;
using Unity.Netcode;
using UnityEngine;

namespace MimicGame.Mimic
{
    /// <summary>Bir oyuncunun hareket "parmak izi" (üstel hareketli ortalamalar).</summary>
    public class MovementProfile
    {
        public float walkSpeed = 3.5f;
        public float sprintSpeed = 6f;
        public float sprintRatio = 0.15f;
        public float crouchRatio = 0.1f;
        public float stillRatio = 0.2f;
        public float avgStillDuration = 1.5f;
        public float flashlightRatio = 0.5f;
        /// <summary>Son ölçülen yatay hız (m/sn).</summary>
        public float currentSpeed;

        // Dahili durum
        internal Vector3 lastPos;
        internal bool hasLast;
        internal float stillTimer;
    }

    /// <summary>Yalnızca sunucuda: her 0.2 sn'de oyuncuları örnekler ve profilleri günceller.</summary>
    public class MovementProfiler : MonoBehaviour
    {
        const float Interval = 0.2f;
        const float SprintThreshold = 4.5f;
        const float WalkMin = 0.5f;
        const float StillThreshold = 0.3f;
        const float SpeedAlpha = 0.12f;
        const float RatioAlpha = 0.02f;

        static readonly Dictionary<ulong, MovementProfile> profiles = new Dictionary<ulong, MovementProfile>();
        static readonly MovementProfile fallback = new MovementProfile();

        float next;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            if (FindFirstObjectByType<MovementProfiler>() != null) return;
            var go = new GameObject("MovementProfiler");
            DontDestroyOnLoad(go);
            go.AddComponent<MovementProfiler>();
        }

        /// <summary>Oyuncunun profili; veri yoksa varsayılanlarla yeni profil oluşturur.</summary>
        public static MovementProfile Get(ulong clientId)
        {
            if (profiles.TryGetValue(clientId, out var p)) return p;
            p = new MovementProfile();
            profiles[clientId] = p;
            return p;
        }

        void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || !nm.IsListening)
            {
                if (profiles.Count > 0) profiles.Clear();
                return;
            }
            if (Time.unscaledTime < next) return;
            float dt = Time.unscaledTime - next + Interval; // gerçek geçen süre (yaklaşık)
            next = Time.unscaledTime + Interval;
            if (dt < 0.05f || dt > 1f) dt = Interval;

            var all = PlayerNetwork.All;
            for (int i = 0; i < all.Count; i++)
            {
                var pn = all[i];
                if (pn == null || !pn.IsSpawned) continue;
                Sample(pn, Get(pn.OwnerClientId), dt);
            }
        }

        static void Sample(PlayerNetwork pn, MovementProfile prof, float dt)
        {
            Vector3 pos = pn.transform.position;
            if (!prof.hasLast) { prof.lastPos = pos; prof.hasLast = true; return; }

            var d = pos - prof.lastPos;
            d.y = 0f;
            prof.lastPos = pos;
            float speed = d.magnitude / dt;
            if (speed > 20f) return; // ışınlanma sıçramasını yoksay
            prof.currentSpeed = speed;

            bool crouch = pn.IsCrouchingNet;
            var fl = pn.GetComponent<Flashlight>();
            bool light = fl != null && fl.IsOn;

            if (!crouch && speed > WalkMin && speed < SprintThreshold)
                prof.walkSpeed = Mathf.Lerp(prof.walkSpeed, speed, SpeedAlpha);
            else if (speed >= SprintThreshold)
                prof.sprintSpeed = Mathf.Lerp(prof.sprintSpeed, speed, SpeedAlpha);

            prof.sprintRatio = Mathf.Lerp(prof.sprintRatio, speed >= SprintThreshold ? 1f : 0f, RatioAlpha);
            prof.crouchRatio = Mathf.Lerp(prof.crouchRatio, crouch ? 1f : 0f, RatioAlpha);
            prof.flashlightRatio = Mathf.Lerp(prof.flashlightRatio, light ? 1f : 0f, RatioAlpha);

            bool still = speed < StillThreshold;
            prof.stillRatio = Mathf.Lerp(prof.stillRatio, still ? 1f : 0f, RatioAlpha);
            if (still) prof.stillTimer += dt;
            else
            {
                if (prof.stillTimer > 0.5f)
                    prof.avgStillDuration = Mathf.Clamp(Mathf.Lerp(prof.avgStillDuration, prof.stillTimer, 0.2f), 0.5f, 8f);
                prof.stillTimer = 0f;
            }
        }
    }
}
