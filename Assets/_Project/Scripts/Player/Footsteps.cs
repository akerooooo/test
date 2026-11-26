using MimicGame.Level;
using MimicGame.Mimic;
using MimicGame.Network;
using Unity.Netcode;
using UnityEngine;

namespace MimicGame.Player
{
    /// <summary>
    /// Ayak sesleri: oyuncu (sahip ve uzak) ve taklitçide aynı bileşen, aynı parametreler.
    /// Adım, konumun yatay değişiminden türetilir (NetworkTransform / ajan hareketi fark etmez).
    /// Klipler prosedürel üretilir: beton (donuk vuruş) ve metal (parlak, çınlayan; merdiven/kedi yolu).
    /// </summary>
    public class Footsteps : MonoBehaviour
    {
        const float WalkStride = 1.75f;    // yürürken adım uzunluğu (3.5 m/sn → 0.5 sn)
        const float SprintStride = 2.0f;   // koşarken (6 m/sn → 0.33 sn)
        const float CrouchStride = 1.26f;  // eğilirken (1.8 m/sn → 0.7 sn)
        const float SprintSpeed = 4.5f;
        const float MinSpeed = 0.6f;
        const float TeleportSpeed = 9f;

        /// <summary>Yalnızca sunucuda: bir adım atıldı (konum, gürültü 0-1). Gelecekteki canavarlar için.</summary>
        public static event System.Action<Vector3, float> ServerStep;

        static AudioClip[] concreteClips;
        static AudioClip[] metalClips;

        AudioSource source;
        PlayerNetwork player;
        MimicAI mimic;
        NetworkObject netObj;
        Vector3 last;
        bool hasLast;
        float accum;
        float smoothSpeed;

        void Awake()
        {
            player = GetComponent<PlayerNetwork>();
            mimic = GetComponent<MimicAI>();
            netObj = GetComponent<NetworkObject>();
            EnsureClips();

            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 1.5f;
            source.maxDistance = 20f;
            source.reverbZoneMix = 1f;
            source.dopplerLevel = 0f;
            source.volume = 1f;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f || source == null) return;
            if (netObj != null && !netObj.IsSpawned) { hasLast = false; return; }

            Vector3 pos = transform.position;
            if (!hasLast) { last = pos; hasLast = true; accum = 0f; return; }
            Vector3 d = pos - last;
            last = pos;
            d.y = 0f;
            float dist = d.magnitude;
            float speed = dist / dt;
            if (speed > TeleportSpeed) { accum = 0f; return; } // ışınlanma / kapıdan geçiş
            smoothSpeed = Mathf.Lerp(smoothSpeed, speed, 1f - Mathf.Exp(-12f * dt));

            if (player != null && player.IsDead) { accum = 0f; return; }

            if (smoothSpeed < MinSpeed) { accum = Mathf.Min(accum, 0.4f); return; }

            // Havada (zıplama / düşme) adım yok
            if (!Raycast(pos, out var hit)) return;

            bool crouch = player != null ? player.IsCrouchingNet : (mimic != null && mimic.IsCrouching);
            bool sprint = !crouch && smoothSpeed > SprintSpeed;
            float stride = crouch ? CrouchStride : (sprint ? SprintStride : WalkStride);

            accum += dist;
            if (accum < stride) return;
            accum -= stride;
            if (accum > stride) accum = 0f;

            float loud = crouch ? 0.35f : (sprint ? 1f : 0.7f);
            PlayStep(pos, loud, IsMetal(hit, pos));
        }

        bool Raycast(Vector3 pos, out RaycastHit hit)
        {
            // Köken kendi kapsülünün içinde olduğundan kendi collider'ını yakalamaz
            return Physics.Raycast(pos + Vector3.up * 0.2f, Vector3.down, out hit, 0.7f, ~0, QueryTriggerInteraction.Ignore);
        }

        // Zemin metal mi: collider adı / üst adı / malzeme adı "Merdiven", "Metal", "Korkuluk" içerir; merdiven odası da metal sayılır
        bool IsMetal(RaycastHit hit, Vector3 pos)
        {
            var col = hit.collider;
            if (col != null)
            {
                if (HasMetalWord(col.name)) return true;
                var parent = col.transform.parent;
                if (parent != null && HasMetalWord(parent.name)) return true;
                var rend = col.GetComponent<Renderer>();
                if (rend != null)
                {
                    var mat = rend.sharedMaterial;
                    if (mat != null && HasMetalWord(mat.name)) return true;
                }
            }
            if (FacilityGenerator.IsInteriorPoint(pos))
            {
                var room = FacilityGenerator.RoomAt(pos);
                if (room != null && room.kind == RoomKind.Stair) return true;
            }
            return false;
        }

        static bool HasMetalWord(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            return n.IndexOf("Merdiven", System.StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Metal", System.StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Korkuluk", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void PlayStep(Vector3 pos, float loud, bool metal)
        {
            var set = metal ? metalClips : concreteClips;
            if (set == null || set.Length == 0) return;
            var clip = set[Random.Range(0, set.Length)];
            if (clip == null) return;
            source.pitch = Random.Range(0.92f, 1.08f);
            source.PlayOneShot(clip, Mathf.Clamp01(loud * 0.85f * Random.Range(0.9f, 1.1f)));

            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer) ServerStep?.Invoke(pos, loud * (metal ? 1.2f : 1f));
        }

        // ---------- Prosedürel klipler ----------

        static void EnsureClips()
        {
            if (concreteClips != null && concreteClips.Length > 0 && concreteClips[0] != null &&
                metalClips != null && metalClips.Length > 0 && metalClips[0] != null) return;
            concreteClips = new AudioClip[4];
            for (int i = 0; i < concreteClips.Length; i++) concreteClips[i] = MakeConcrete(1000 + i * 37);
            metalClips = new AudioClip[3];
            for (int i = 0; i < metalClips.Length; i++) metalClips[i] = MakeMetal(2000 + i * 53);
        }

        // Kısa, donuk vuruş: alçak geçiren gürültü + alçak "tok" ses, hızlı sönüm
        static AudioClip MakeConcrete(int seed)
        {
            const int sr = 44100;
            int n = (int)(sr * 0.16f);
            var data = new float[n];
            var rng = new System.Random(seed);
            float lp = 0f;
            float cutoff = 0.22f + (float)rng.NextDouble() * 0.16f;
            float thumpHz = 70f + (float)rng.NextDouble() * 30f;
            float decay = 34f + (float)rng.NextDouble() * 14f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                lp += (noise - lp) * cutoff;
                float env = Mathf.Exp(-t * decay);
                float thump = Mathf.Sin(2f * Mathf.PI * thumpHz * t) * Mathf.Exp(-t * 42f) * 0.8f;
                float attack = Mathf.Clamp01(i / 24f);
                data[i] = (lp * 1.4f * env + thump) * attack * 0.7f;
            }
            var clip = AudioClip.Create("Adım_Beton_" + seed, n, 1, sr, false);
            clip.SetData(data, 0);
            return clip;
        }

        // Parlak vuruş + çınlayan tonlar (metal merdiven / kedi yolu)
        static AudioClip MakeMetal(int seed)
        {
            const int sr = 44100;
            int n = (int)(sr * 0.4f);
            var data = new float[n];
            var rng = new System.Random(seed);
            float lp = 0f;
            float f1 = 1100f + (float)rng.NextDouble() * 200f;
            float f2 = f1 * 1.47f;
            float f3 = f1 * 2.06f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                lp += (noise - lp) * 0.35f;
                float high = noise - lp; // yüksek geçiren
                float click = high * Mathf.Exp(-t * 90f);
                float ring = (Mathf.Sin(2f * Mathf.PI * f1 * t) * 0.5f
                            + Mathf.Sin(2f * Mathf.PI * f2 * t) * 0.3f
                            + Mathf.Sin(2f * Mathf.PI * f3 * t) * 0.2f) * Mathf.Exp(-t * 13f);
                float thump = Mathf.Sin(2f * Mathf.PI * 110f * t) * Mathf.Exp(-t * 38f) * 0.6f;
                float attack = Mathf.Clamp01(i / 16f);
                data[i] = (click * 0.9f + ring * 0.35f + thump) * attack * 0.6f;
            }
            var clip = AudioClip.Create("Adım_Metal_" + seed, n, 1, sr, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
