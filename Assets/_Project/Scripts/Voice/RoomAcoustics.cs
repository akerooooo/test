using MimicGame.Items;
using MimicGame.Mimic;
using UnityEngine;

namespace MimicGame.Voice
{
    /// <summary>
    /// Yerel oyuncunun kamerasına eklenir. Çevredeki boşluğu ışınlarla ölçer ve odanın büyüklüğüne
    /// göre bir AudioReverbZone'u (kullanıcı ön ayarı) yumuşakça ayarlar. Dışarıda yankı yoktur.
    /// </summary>
    public class RoomAcoustics : MonoBehaviour
    {
        const int HorizontalRays = 12;
        const float MaxDistance = 40f;
        const float CeilingMax = 12f;
        const int IndoorMinHits = 8;
        const float MeasureInterval = 0.25f;
        const float Smoothing = 6f; // ~0.5 sn'de oturur

        struct Reverb
        {
            public float room, roomHF, decay, decayHFRatio, reflections, reflDelay, reverb, reverbDelay, diffusion, density;
        }

        AudioReverbZone zone;
        Vector3[] dirs;
        readonly RaycastHit[] hits = new RaycastHit[16];
        float nextMeasure;
        Reverb cur, target;

        /// <summary>Son ölçülen oda büyüklüğü (m) ve iç mekan durumu (tanı için).</summary>
        public float RoomSize { get; private set; }
        public bool Indoor { get; private set; }

        /// <summary>Kameraya ekler (zaten varsa mevcut olanı döndürür).</summary>
        public static RoomAcoustics Attach(GameObject go)
        {
            if (go == null) return null;
            var ra = go.GetComponent<RoomAcoustics>();
            if (ra == null) ra = go.AddComponent<RoomAcoustics>();
            return ra;
        }

        /// <summary>Kameradan kaldırır (bölge de silinir).</summary>
        public static void Detach(GameObject go)
        {
            if (go == null) return;
            var ra = go.GetComponent<RoomAcoustics>();
            if (ra != null) Destroy(ra);
        }

        void Awake()
        {
            // Dünya eksenlerinde: oyuncu dönünce ölçüm titremesin
            var list = new System.Collections.Generic.List<Vector3>();
            for (int i = 0; i < HorizontalRays; i++)
            {
                float a = i * (360f / HorizontalRays) * Mathf.Deg2Rad;
                list.Add(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)));
            }
            list.Add(Vector3.up);
            const float d = 0.70710678f;
            list.Add(new Vector3(d, d, 0f));
            list.Add(new Vector3(-d, d, 0f));
            list.Add(new Vector3(0f, d, d));
            list.Add(new Vector3(0f, d, -d));
            dirs = list.ToArray();

            zone = GetComponent<AudioReverbZone>();
            if (zone == null) zone = gameObject.AddComponent<AudioReverbZone>();
            zone.minDistance = 1000f;
            zone.maxDistance = 1001f;
            zone.reverbPreset = AudioReverbPreset.User;

            target = Outdoor();
            cur = target;
            Apply(cur);
        }

        void OnDestroy()
        {
            if (zone != null) Destroy(zone);
        }

        void Update()
        {
            if (Time.time >= nextMeasure)
            {
                nextMeasure = Time.time + MeasureInterval;
                Measure();
            }

            float t = 1f - Mathf.Exp(-Smoothing * Time.deltaTime);
            cur = Lerp(cur, target, t);
            Apply(cur);
        }

        // ---------- Ölçüm ----------

        void Measure()
        {
            Vector3 origin = transform.position;
            int horizHits = 0;
            float horizSum = 0f;
            float up = MaxDistance;
            int diagHits = 0;

            for (int i = 0; i < dirs.Length; i++)
            {
                float d = Cast(origin, dirs[i]);
                bool hit = d < MaxDistance;
                if (i < HorizontalRays)
                {
                    horizSum += d; // ıskalar 40 sayılır
                    if (hit) horizHits++;
                }
                else if (i == HorizontalRays) up = d;
                else if (hit && d < 16f) diagHits++;
            }

            float size = horizSum / HorizontalRays;
            // Tavan: dik ışın 12 m içinde vurur; açıklık (ör. ışıklık) için çapraz ışınlar da destekler
            bool ceiling = up <= CeilingMax || diagHits >= 3;
            bool indoor = ceiling && horizHits >= IndoorMinHits;

            RoomSize = size;
            Indoor = indoor;
            target = indoor ? ForSize(size) : Outdoor();
        }

        // En yakın geçerli çarpışma mesafesi (oyuncu kapsülü, eşya ve taklitçi yok sayılır)
        float Cast(Vector3 origin, Vector3 dir)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, hits, MaxDistance, ~0, QueryTriggerInteraction.Ignore);
            float best = MaxDistance;
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                var col = h.collider;
                if (col == null || h.distance >= best) continue;
                if (col is CharacterController) continue;
                if (col.transform.root == transform.root) continue;
                if (col.GetComponentInParent<Item>() != null) continue;
                if (col.GetComponentInParent<MimicAI>() != null) continue;
                best = h.distance;
            }
            return best;
        }

        // ---------- Eşleme ----------

        static Reverb Outdoor()
        {
            return new Reverb
            {
                room = -10000f, roomHF = -10000f, decay = 1f, decayHFRatio = 0.5f,
                reflections = -10000f, reflDelay = 0f, reverb = -10000f, reverbDelay = 0.01f,
                diffusion = 100f, density = 100f,
            };
        }

        static Reverb ForSize(float s)
        {
            if (s <= 3f)
            {
                // Küçük oda: kısa çınlama
                return new Reverb
                {
                    room = -1400f, roomHF = -700f, decay = 0.6f, decayHFRatio = 0.7f,
                    reflections = -1000f, reflDelay = 0.006f, reverb = -900f, reverbDelay = 0.012f,
                    diffusion = 100f, density = 100f,
                };
            }
            if (s <= 8f)
            {
                // Orta: 3-8 m arası 1.2 -> 1.8 sn
                float t = (s - 3f) / 5f;
                return new Reverb
                {
                    room = Mathf.Lerp(-1300f, -1000f, t),
                    roomHF = Mathf.Lerp(-800f, -900f, t),
                    decay = Mathf.Lerp(1.2f, 1.8f, t),
                    decayHFRatio = Mathf.Lerp(0.7f, 0.6f, t),
                    reflections = Mathf.Lerp(-900f, -600f, t),
                    reflDelay = Mathf.Lerp(0.012f, 0.025f, t),
                    reverb = Mathf.Lerp(-600f, -300f, t),
                    reverbDelay = Mathf.Lerp(0.02f, 0.04f, t),
                    diffusion = 100f, density = 100f,
                };
            }
            {
                // Büyük/boş salon: 8-25 m arası 2.5 -> 3.5 sn
                float t = Mathf.Clamp01((s - 8f) / 17f);
                return new Reverb
                {
                    room = Mathf.Lerp(-900f, -600f, t),
                    roomHF = Mathf.Lerp(-1000f, -1200f, t),
                    decay = Mathf.Lerp(2.5f, 3.5f, t),
                    decayHFRatio = Mathf.Lerp(0.6f, 0.5f, t),
                    reflections = Mathf.Lerp(-500f, -200f, t),
                    reflDelay = Mathf.Lerp(0.04f, 0.07f, t),
                    reverb = Mathf.Lerp(-200f, 200f, t),
                    reverbDelay = Mathf.Lerp(0.05f, 0.09f, t),
                    diffusion = 100f, density = 100f,
                };
            }
        }

        static Reverb Lerp(Reverb a, Reverb b, float t)
        {
            return new Reverb
            {
                room = Mathf.Lerp(a.room, b.room, t),
                roomHF = Mathf.Lerp(a.roomHF, b.roomHF, t),
                decay = Mathf.Lerp(a.decay, b.decay, t),
                decayHFRatio = Mathf.Lerp(a.decayHFRatio, b.decayHFRatio, t),
                reflections = Mathf.Lerp(a.reflections, b.reflections, t),
                reflDelay = Mathf.Lerp(a.reflDelay, b.reflDelay, t),
                reverb = Mathf.Lerp(a.reverb, b.reverb, t),
                reverbDelay = Mathf.Lerp(a.reverbDelay, b.reverbDelay, t),
                diffusion = Mathf.Lerp(a.diffusion, b.diffusion, t),
                density = Mathf.Lerp(a.density, b.density, t),
            };
        }

        void Apply(Reverb r)
        {
            if (zone == null) return;
            zone.room = Mathf.RoundToInt(Mathf.Clamp(r.room, -10000f, 0f));
            zone.roomHF = Mathf.RoundToInt(Mathf.Clamp(r.roomHF, -10000f, 0f));
            zone.decayTime = Mathf.Clamp(r.decay, 0.1f, 20f);
            zone.decayHFRatio = Mathf.Clamp(r.decayHFRatio, 0.1f, 2f);
            zone.reflections = Mathf.RoundToInt(Mathf.Clamp(r.reflections, -10000f, 1000f));
            zone.reflectionsDelay = Mathf.Clamp(r.reflDelay, 0f, 0.3f);
            zone.reverb = Mathf.RoundToInt(Mathf.Clamp(r.reverb, -10000f, 2000f));
            zone.reverbDelay = Mathf.Clamp(r.reverbDelay, 0f, 0.1f);
            zone.diffusion = Mathf.Clamp(r.diffusion, 0f, 100f);
            zone.density = Mathf.Clamp(r.density, 0f, 100f);
        }
    }
}
