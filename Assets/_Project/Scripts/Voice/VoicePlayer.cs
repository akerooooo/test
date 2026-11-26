using MimicGame.Items;
using MimicGame.Mimic;
using UnityEngine;

namespace MimicGame.Voice
{
    /// <summary>
    /// Tüm uzak ses çalmanın tek noktası. PCM float örnekleri alır, 3B olarak çalar.
    /// Örneklerin ağdan mı kayıttan mı geldiğini bilmez (ileride taklitçiler aynı yolu kullanır).
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class VoicePlayer : MonoBehaviour
    {
        [Header("Çıkış")]
        /// <summary>Çıkış hızı; Awake'te AudioSettings.outputSampleRate'ten alınır.</summary>
        [HideInInspector] public int sampleRate = 48000;
        [Range(0f, 1f)] public float volume = 1f;

        [Header("Mesafe")]
        public float minDistance = 1.5f;
        public float maxDistance = 25f;

        [Header("Duvar boğuklaştırma")]
        public float occludedCutoff = 1200f;
        public float openCutoff = 22000f;
        public float occludedVolume = 0.6f;
        public LayerMask occlusionMask = ~0;

        const float PrebufferSeconds = 0.15f; // ağ dalgalanmasına karşı tampon
        const float MaxBufferSeconds = 0.6f;
        const float CheckInterval = 0.1f;
        const float StreamAheadSeconds = 0.3f; // klip akışında tamponda tutulacak miktar
        const float ArrayChunkSeconds = 0.1f;

        AudioSource source;
        AudioLowPassFilter lowPass;
        AudioClip clip;

        // Halka tampon (ses ve ana iş parçacığı arasında; hepsi lock altında)
        readonly object sync = new object();
        float[] ring;
        int head, count;
        bool draining, flush;
        int prebufferSamples, maxBufferSamples;

        // Yeniden örnekleme durumu (lock altında)
        int lastInRate;
        double resPos;
        float resPrev;
        float[] scratch = new float[2048];

        volatile float level;

        // Klip akışı (yalnızca ana iş parçacığı)
        VoiceClip streamClip;
        int streamPacket;
        float[] streamSamples;
        int streamRate, streamPos;
        float[] streamDecode = new float[4096];
        bool streaming;

        // Tıkanma
        static AudioListener cachedListener;
        readonly RaycastHit[] hits = new RaycastHit[16];
        float nextCheck;
        float targetCutoff, targetMul = 1f;
        float curCutoff, curMul = 1f;

        /// <summary>Tamponda çalınacak ses var mı (ileride taklitçi mantığı için).</summary>
        public bool IsPlaying
        {
            get
            {
                if (streaming) return true;
                lock (sync) return count > 0 || draining;
            }
        }
        /// <summary>Son okunan bloğun RMS değeri (ileride taklitçi mantığı için).</summary>
        public float CurrentLevel => level;

        void Awake()
        {
            int outRate = AudioSettings.outputSampleRate;
            sampleRate = outRate > 0 ? outRate : 48000;
            source = GetComponent<AudioSource>();
            lowPass = GetComponent<AudioLowPassFilter>();
            if (lowPass == null) lowPass = gameObject.AddComponent<AudioLowPassFilter>();

            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            source.loop = true;
            source.playOnAwake = false;
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            source.rolloffMode = AudioRolloffMode.Custom;
            float k = Mathf.Clamp(minDistance / maxDistance, 0.01f, 0.9f);
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(k, 1f), new Keyframe(0.2f, 0.3f),
                new Keyframe(0.5f, 0.08f), new Keyframe(1f, 0f)));

            prebufferSamples = Mathf.Max(1, Mathf.RoundToInt(sampleRate * PrebufferSeconds));
            maxBufferSamples = Mathf.Max(prebufferSamples * 2, Mathf.RoundToInt(sampleRate * MaxBufferSeconds));
            ring = new float[sampleRate * 2];

            curCutoff = targetCutoff = openCutoff;
            lowPass.cutoffFrequency = curCutoff;

            clip = AudioClip.Create("Voice", sampleRate * 2, 1, sampleRate, true, OnPcmRead);
            source.clip = clip;
        }

        void OnEnable()
        {
            if (source != null && clip != null && !source.isPlaying) source.Play();
        }

        void OnDisable()
        {
            if (source != null) source.Stop();
            lock (sync) { head = 0; count = 0; draining = false; flush = false; }
            StopStream();
        }

        void OnDestroy()
        {
            if (clip != null) Destroy(clip);
        }

        /// <summary>Örnekleri (mono, -1..1) ekler. Gerekirse sampleRate'e çevirir. İş parçacığı güvenli.</summary>
        public void PushSamples(float[] samples, int inputCount, int inputSampleRate)
        {
            if (samples == null || inputCount <= 0 || inputSampleRate <= 0) return;
            if (inputCount > samples.Length) inputCount = samples.Length;

            lock (sync)
            {
                if (inputSampleRate == sampleRate)
                {
                    Enqueue(samples, inputCount);
                    return;
                }

                // Doğrusal yeniden örnekleme (parçalar arası süreklilikle)
                if (inputSampleRate != lastInRate) { lastInRate = inputSampleRate; resPos = 0; resPrev = samples[0]; }
                double step = (double)inputSampleRate / sampleRate;
                int maxOut = (int)(inputCount / step) + 2;
                if (scratch.Length < maxOut) scratch = new float[maxOut];

                int n = 0;
                double p = resPos;
                while (p < inputCount - 1 && n < scratch.Length)
                {
                    int i = (int)System.Math.Floor(p);
                    float a = i < 0 ? resPrev : samples[i];
                    float b = samples[i + 1];
                    scratch[n++] = a + (b - a) * (float)(p - i);
                    p += step;
                }
                resPos = p - inputCount;
                resPrev = samples[inputCount - 1];
                Enqueue(scratch, n);
            }
        }

        /// <summary>Ham örnekleri akıtarak çalar (taklitçiler için). Zaten bir şey çalıyorsa false döner.</summary>
        public bool PlayClip(float[] samples, int inputSampleRate)
        {
            if (samples == null || samples.Length == 0 || inputSampleRate <= 0) return false;
            if (IsPlaying) return false;
            StopStream();
            streamSamples = samples;
            streamRate = inputSampleRate;
            streamPos = 0;
            streaming = true;
            FeedStream();
            return true;
        }

        /// <summary>Kayıtlı bir klibi (ses paketleri) çözerek akıtır. Zaten bir şey çalıyorsa false döner.</summary>
        public bool PlayClip(VoiceClip clip)
        {
            if (clip == null || clip.packets == null || clip.packets.Count == 0) return false;
            if (IsPlaying) return false;
            StopStream();
            streamClip = clip;
            streamPacket = 0;
            streaming = true;
            FeedStream();
            return true;
        }

        // Klip bitti: ön-tamponlamayı beklemeden kalan kuyruğu da çal
        void EndStream()
        {
            lock (sync) flush = count > 0;
            StopStream();
        }

        void StopStream()
        {
            streaming = false;
            streamClip = null;
            streamSamples = null;
            streamPacket = 0;
            streamPos = 0;
        }

        int Buffered()
        {
            lock (sync) return count;
        }

        // Tamponu ~0.3 sn ileride tutacak kadar çöz ve ekle
        void FeedStream()
        {
            if (!streaming) return;
            int target = Mathf.RoundToInt(sampleRate * StreamAheadSeconds);
            for (int guard = 0; guard < 64 && Buffered() < target; guard++)
            {
                if (streamClip != null)
                {
                    var pk = streamClip.packets;
                    if (streamPacket >= pk.Count) { EndStream(); return; }
                    var (codec, data) = pk[streamPacket++];
                    if (data == null) continue;
                    if (!VoiceCodec.Decode(codec, data, data.Length, ref streamDecode, out int n, out int rate)) continue;
                    PushSamples(streamDecode, n, rate);
                }
                else if (streamSamples != null)
                {
                    int chunk = Mathf.Max(1, Mathf.RoundToInt(streamRate * ArrayChunkSeconds));
                    int len = Mathf.Min(chunk, streamSamples.Length - streamPos);
                    if (len <= 0) { EndStream(); return; }
                    if (streamDecode.Length < len) streamDecode = new float[len];
                    System.Array.Copy(streamSamples, streamPos, streamDecode, 0, len);
                    streamPos += len;
                    PushSamples(streamDecode, len, streamRate);
                }
                else { StopStream(); return; }
            }
        }

        // lock altında çağrılır
        void Enqueue(float[] src, int n)
        {
            if (n > ring.Length) { src = SliceTail(src, n, ring.Length); n = ring.Length; }
            // Gecikme sınırı: fazlalığı en eskiden at
            int overflow = count + n - maxBufferSamples;
            if (overflow > 0)
            {
                int drop = Mathf.Min(overflow, count);
                head = (head + drop) % ring.Length;
                count -= drop;
            }
            if (count + n > ring.Length) // yeni parça tek başına sınırı aşıyorsa
            {
                int drop = count + n - ring.Length;
                head = (head + drop) % ring.Length;
                count -= drop;
            }
            int tail = (head + count) % ring.Length;
            int first = Mathf.Min(n, ring.Length - tail);
            System.Array.Copy(src, 0, ring, tail, first);
            if (first < n) System.Array.Copy(src, first, ring, 0, n - first);
            count += n;
        }

        static float[] SliceTail(float[] src, int n, int keep)
        {
            var r = new float[keep];
            System.Array.Copy(src, n - keep, r, 0, keep);
            return r;
        }

        // Ses iş parçacığı: Unity API'si çağırma
        void OnPcmRead(float[] data)
        {
            int len = data.Length;
            int got = 0;
            lock (sync)
            {
                bool resumed = false;
                if (!draining && count > 0 && (count >= prebufferSamples || flush)) { draining = true; resumed = true; }
                if (draining)
                {
                    got = Mathf.Min(count, len);
                    int first = Mathf.Min(got, ring.Length - head);
                    System.Array.Copy(ring, head, data, 0, first);
                    if (first < got) System.Array.Copy(ring, 0, data, first, got - first);
                    head = (head + got) % ring.Length;
                    count -= got;
                    if (count == 0) { draining = false; flush = false; } // tekrar ön-tamponlama
                }

                // Tıkırtı olmasın: yeniden başlarken yumuşak giriş, tampon bitince yumuşak çıkış
                const int Fade = 96;
                if (resumed)
                {
                    int n = Mathf.Min(Fade, got);
                    for (int i = 0; i < n; i++) data[i] *= (i + 1f) / n;
                }
                if (got > 0 && got < len)
                {
                    int n = Mathf.Min(Fade, got);
                    for (int i = 0; i < n; i++) data[got - 1 - i] *= (i + 1f) / n;
                }
            }
            for (int i = got; i < len; i++) data[i] = 0f;

            float sum = 0f;
            for (int i = 0; i < got; i++) sum += data[i] * data[i];
            level = got > 0 ? Mathf.Sqrt(sum / got) : 0f;
        }

        void Update()
        {
            FeedStream();
            if (Time.time >= nextCheck)
            {
                nextCheck = Time.time + CheckInterval;
                bool blocked = IsOccluded();
                targetCutoff = blocked ? occludedCutoff : openCutoff;
                targetMul = blocked ? occludedVolume : 1f;
            }

            float t = 1f - Mathf.Exp(-10f * Time.deltaTime);
            curCutoff = Mathf.Lerp(curCutoff, targetCutoff, t);
            curMul = Mathf.Lerp(curMul, targetMul, t);
            lowPass.cutoffFrequency = curCutoff;
            source.volume = Mathf.Clamp01(volume) * curMul;
        }

        bool IsOccluded()
        {
            var listener = FindListener();
            if (listener == null) return false;

            Vector3 from = listener.transform.position;
            Vector3 to = transform.position;
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 0.01f) return false;

            int n = Physics.RaycastNonAlloc(from, dir / dist, hits, dist, occlusionMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var col = hits[i].collider;
                if (col == null) continue;
                if (col is CharacterController) continue;
                if (col.GetComponentInParent<Item>() != null) continue;
                if (col.GetComponentInParent<MimicAI>() != null) continue; // taklitçinin kendi kapsülü
                return true;
            }
            return false;
        }

        // Etkin AudioListener'ı bul (uzak oyuncuların kamerasındaki kapalıdır)
        static AudioListener FindListener()
        {
            if (cachedListener != null && cachedListener.isActiveAndEnabled) return cachedListener;
            cachedListener = null;
            var all = FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
                if (all[i].isActiveAndEnabled) { cachedListener = all[i]; break; }
            return cachedListener;
        }
    }
}
