using MimicGame.Items;
using MimicGame.Mimic;
using UnityEngine;

namespace MimicGame.Voice
{
    /// <summary>
    /// Tüm uzak ses çalmanın tek noktası. PCM float örnekleri alır, 3B olarak çalar.
    /// Örneklerin ağdan mı kayıttan mı geldiğini bilmez (taklitçiler aynı yolu kullanır).
    /// Uyarlanabilir jitter tamponu, hız ayarıyla kayma düzeltme ve Hermite yeniden örnekleme içerir.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class VoicePlayer : MonoBehaviour
    {
        [Header("Çıkış")]
        /// <summary>Çıkış hızı; Awake'te AudioSettings.outputSampleRate'ten alınır.</summary>
        [HideInInspector] public int sampleRate = 48000;
        [Range(0f, 1f)] public float volume = 1f;

        // Sahnede eski değer kalmasın diye serileştirilmez
        [System.NonSerialized] public float minDistance = 1.5f;
        [System.NonSerialized] public float maxDistance = 40f;

        [Header("Duvar boğuklaştırma")]
        public float occludedCutoff = 1200f;
        public float openCutoff = 22000f;
        public float occludedVolume = 0.6f;
        public LayerMask occlusionMask = ~0;

        // Mesafe / hava emilimi
        const float AirNearDistance = 8f;
        const float AirFarCutoff = 5000f;
        const float FadeTailFraction = 0.3f; // maxDistance'ın son %30'u yumuşakça sıfıra iner

        // Jitter tamponu
        const float TargetStart = 0.12f;
        const float TargetStep = 0.03f;
        const float TargetMax = 0.35f;
        const float TargetMin = 0.08f;
        const float TargetDecay = 0.01f;
        const float DecayInterval = 5f;
        const float DriftMax = 0.05f;
        const float SkipSeconds = 1.0f;      // bundan fazla birikirse ileri atla
        const float SkipFade = 0.010f;       // atlama çapraz geçişi
        const float EdgeFade = 0.005f;       // yumuşak giriş/çıkış
        const float GapReprebuffer = 0.25f;  // bundan uzun boşluktan sonra yeniden ön-tamponla
        const float RingSeconds = 3f;

        const float CheckInterval = 0.1f;
        const float ArrayChunkSeconds = 0.1f;

        enum PlayState { Prebuffer, Playing, Gap }

        AudioSource source;
        AudioLowPassFilter lowPass;
        AudioClip clip;

        // Halka tampon: GİRİŞ hızında örnekler (hepsi lock altında)
        readonly object sync = new object();
        float[] ring;
        int ringRate;
        int head, count;
        PlayState state = PlayState.Prebuffer;
        bool flush;
        bool driftEnabled = true;
        double frac;           // p0 ile p1 arası kesirli konum
        float hist;            // p[-1]
        float lastOut, fade;
        int gapSamples;
        double smoothRatio = 1.0;
        int underruns;
        float targetSeconds = TargetStart;

        volatile float level;
        float calmTimer;

        // Klip akışı (yalnızca ana iş parçacığı)
        VoiceClip streamClip;
        int streamPacket;
        float[] streamSamples;
        int streamRate, streamPos;
        float[] streamDecode = new float[4096];
        bool streaming;
        bool streamDecodedAny;

        // Tıkanma / mesafe
        static AudioListener cachedListener;
        readonly RaycastHit[] hits = new RaycastHit[16];
        float nextCheck;
        float targetCutoff, targetMul = 1f;
        float curCutoff, curMul = 1f;

        /// <summary>Tamponda çalınacak ses var mı (taklitçi mantığı için).</summary>
        public bool IsPlaying
        {
            get
            {
                if (streaming) return true;
                lock (sync) return state == PlayState.Playing || count >= 3 || (flush && count > 0);
            }
        }
        /// <summary>Son okunan bloğun RMS değeri.</summary>
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
            source.reverbZoneMix = 1f; // oda yankısı alsın
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            source.rolloffMode = AudioRolloffMode.Custom;
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, BuildRolloff(minDistance, maxDistance));

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
            lock (sync) ResetPlayback();
            StopStream();
        }

        void OnDestroy()
        {
            if (clip != null) Destroy(clip);
        }

        // lock altında
        void ResetPlayback()
        {
            head = 0; count = 0;
            state = PlayState.Prebuffer;
            flush = false;
            frac = 0.0; hist = 0f; lastOut = 0f; fade = 0f;
            gapSamples = 0;
            smoothRatio = 1.0;
        }

        // ---------- Mesafe eğrisi ----------

        // 1.5 m'ye kadar tam ses, sonra 1.5/d, son %30'da smoothstep ile 0
        static float Gain(float d, float minD, float maxD)
        {
            float g = d <= minD ? 1f : minD / d;
            float tailStart = maxD * (1f - FadeTailFraction);
            if (d > tailStart)
            {
                float t = Mathf.Clamp01((d - tailStart) / (maxD - tailStart));
                g *= 1f - t * t * (3f - 2f * t);
            }
            return d >= maxD ? 0f : g;
        }

        static AnimationCurve BuildRolloff(float minD, float maxD)
        {
            float[] ds = { 0f, minD, 2.2f, 3f, 4f, 6f, 8f, 11f, 14f, 18f, 22f, 25f, maxD * 0.7f, maxD * 0.775f, maxD * 0.85f, maxD * 0.925f, maxD };
            const float eps = 0.002f;
            var keys = new Keyframe[ds.Length];
            for (int i = 0; i < ds.Length; i++)
            {
                float d = ds[i];
                float v = Gain(d, minD, maxD);
                float inT = i == 0 ? 0f : (v - Gain(d - eps, minD, maxD)) / eps * maxD;
                float outT = i == ds.Length - 1 ? 0f : (Gain(d + eps, minD, maxD) - v) / eps * maxD;
                keys[i] = new Keyframe(d / maxD, v, inT, outT);
            }
            return new AnimationCurve(keys);
        }

        // ---------- Giriş ----------

        /// <summary>Örnekleri (mono, -1..1) ekler. Hemen kopyalanır; çağıran dizi sonra serbestçe yeniden kullanılabilir. İş parçacığı güvenli.</summary>
        public void PushSamples(float[] samples, int inputCount, int inputSampleRate)
        {
            if (samples == null || inputCount <= 0 || inputSampleRate <= 0) return;
            if (inputCount > samples.Length) inputCount = samples.Length;

            lock (sync)
            {
                if (inputSampleRate != ringRate || ring == null)
                {
                    // Hız değişti: tamponu ve yeniden örnekleyiciyi sıfırla
                    ringRate = inputSampleRate;
                    ring = new float[Mathf.Max(1024, Mathf.RoundToInt(inputSampleRate * RingSeconds))];
                    ResetPlayback();
                }
                Enqueue(samples, inputCount);
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
            BeginStream();
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
            BeginStream();
            return true;
        }

        void BeginStream()
        {
            streaming = true;
            streamDecodedAny = false;
            lock (sync) driftEnabled = false; // klipte hız oynatma yok
            FeedStream();
        }

        // Klip bitti: ön-tamponlamayı beklemeden kalan kuyruğu da çal
        void EndStream()
        {
            if (!streamDecodedAny) Debug.LogWarning("[Ses] Klip çözülemedi (codec desteklenmiyor olabilir).");
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
            lock (sync) driftEnabled = true;
        }

        float BufferedSeconds()
        {
            lock (sync) return ringRate > 0 ? (float)count / ringRate : 0f;
        }

        float TargetSeconds()
        {
            lock (sync) return targetSeconds;
        }

        // Tamponu hedef + 0.2 sn ileride tutacak kadar çöz ve ekle
        void FeedStream()
        {
            if (!streaming) return;
            float ahead = TargetSeconds() + 0.2f;
            for (int guard = 0; guard < 64 && BufferedSeconds() < ahead; guard++)
            {
                if (streamClip != null)
                {
                    var pk = streamClip.packets;
                    if (streamPacket >= pk.Count) { EndStream(); return; }
                    var (codec, data) = pk[streamPacket++];
                    if (data == null) continue;
                    if (!VoiceCodec.Decode(codec, data, data.Length, ref streamDecode, out int n, out int rate)) continue;
                    streamDecodedAny = true;
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
                    streamDecodedAny = true;
                    PushSamples(streamDecode, len, streamRate);
                }
                else { StopStream(); return; }
            }
        }

        // ---------- Tampon (lock altında) ----------

        void Enqueue(float[] src, int n)
        {
            int total = count + n;
            if (total > ring.Length) Grow(total);
            int len = ring.Length;
            int tail = (head + count) % len;
            int first = Mathf.Min(n, len - tail);
            System.Array.Copy(src, 0, ring, tail, first);
            if (first < n) System.Array.Copy(src, first, ring, 0, n - first);
            count += n;
            SkipAhead();
        }

        void Grow(int need)
        {
            int newLen = Mathf.Max(need, ring.Length * 2);
            var nr = new float[newLen];
            int first = Mathf.Min(count, ring.Length - head);
            System.Array.Copy(ring, head, nr, 0, first);
            if (first < count) System.Array.Copy(ring, 0, nr, first, count - first);
            ring = nr;
            head = 0;
        }

        // Son çare: 1 sn'den fazla birikmişse hedefe kadar ileri atla (10 ms çapraz geçişle)
        void SkipAhead()
        {
            int limit = (int)(ringRate * SkipSeconds);
            if (count <= limit) return;
            int tgt = Mathf.Max(3, (int)(ringRate * targetSeconds));
            int drop = count - tgt;
            int len = ring.Length;
            int k = Mathf.Min(Mathf.Max(1, (int)(ringRate * SkipFade)), tgt);
            for (int i = 0; i < k; i++)
            {
                int a = (head + i) % len;
                int b = (head + drop + i) % len;
                ring[b] = ring[a] + (ring[b] - ring[a]) * ((float)i / k);
            }
            head = (head + drop) % len;
            count -= drop;
        }

        // ---------- Ses iş parçacığı: Unity API'si çağırma ----------

        void OnPcmRead(float[] data)
        {
            int len = data.Length;
            float sumSq = 0f;
            int played = 0;

            lock (sync)
            {
                if (ring == null || ringRate <= 0)
                {
                    System.Array.Clear(data, 0, len);
                    level = 0f;
                    return;
                }

                float[] r = ring;
                int L = r.Length;
                float tgt = targetSeconds;

                // Kayma düzeltme: tampon doluysa biraz hızlı, boşsa yavaş tüket
                double ratio = 1.0;
                if (driftEnabled && !flush && state == PlayState.Playing)
                {
                    float bufSec = (float)count / ringRate;
                    float adj = (bufSec - tgt) / tgt * DriftMax;
                    if (adj > DriftMax) adj = DriftMax; else if (adj < -DriftMax) adj = -DriftMax;
                    ratio = 1.0 + adj;
                }
                smoothRatio += (ratio - smoothRatio) * 0.2;
                double step = (double)ringRate / sampleRate * smoothRatio;

                int targetIn = System.Math.Max(3, (int)(tgt * ringRate));
                int gapLimit = (int)(GapReprebuffer * sampleRate);
                float fadeStep = 1f / System.Math.Max(1f, EdgeFade * sampleRate);

                PlayState st = state;
                int hd = head, cnt = count, gap = gapSamples;
                double fr = frac;
                float hs = hist, last = lastOut, fd = fade;
                bool fl = flush;

                for (int i = 0; i < len; i++)
                {
                    int need = fl ? 1 : 3;
                    if (st == PlayState.Playing && cnt < need) { st = PlayState.Gap; gap = 0; }
                    if (st == PlayState.Gap)
                    {
                        if (cnt >= need)
                        {
                            if (gap > gapLimit) st = PlayState.Prebuffer;          // yeni konuşma
                            else { st = PlayState.Playing; underruns++; }          // akış ortasında tampon boşaldı
                        }
                        else
                        {
                            if (cnt == 0) fl = false;
                            gap++;
                            fd -= fadeStep; if (fd < 0f) fd = 0f;
                            data[i] = last * fd; // son değeri tutarak ~5 ms'de sön
                            continue;
                        }
                    }
                    if (st == PlayState.Prebuffer)
                    {
                        if (cnt >= need && (cnt >= targetIn || fl)) st = PlayState.Playing;
                        else { data[i] = 0f; fd = 0f; last = 0f; continue; }
                    }

                    // Dört noktalı kübik Hermite
                    float pm = hs;
                    float p0 = r[hd];
                    int h1 = hd + 1; if (h1 >= L) h1 = 0;
                    float p1 = cnt > 1 ? r[h1] : p0;
                    int h2 = h1 + 1; if (h2 >= L) h2 = 0;
                    float p2 = cnt > 2 ? r[h2] : p1;
                    float f = (float)fr;
                    float c1 = 0.5f * (p1 - pm);
                    float c2 = pm - 2.5f * p0 + 2f * p1 - 0.5f * p2;
                    float c3 = 0.5f * (p2 - pm) + 1.5f * (p0 - p1);
                    float s = ((c3 * f + c2) * f + c1) * f + p0;

                    fd += fadeStep; if (fd > 1f) fd = 1f;
                    last = s;
                    float o = s * fd;
                    data[i] = o;
                    sumSq += o * o;
                    played++;

                    fr += step;
                    while (fr >= 1.0 && cnt > 0)
                    {
                        fr -= 1.0;
                        hs = r[hd];
                        hd++; if (hd >= L) hd = 0;
                        cnt--;
                    }
                    if (fr >= 1.0) fr = 0.0;
                }

                state = st; head = hd; count = cnt; gapSamples = gap;
                frac = fr; hist = hs; lastOut = last; fade = fd; flush = fl;
            }

            // Yumuşak sınırlayıcı: 0.9 üstünde x/(1+|x|) benzeri diz
            for (int i = 0; i < len; i++)
            {
                float x = data[i];
                float ax = x < 0f ? -x : x;
                if (ax > 0.9f)
                {
                    float u = (ax - 0.9f) * 10f;
                    ax = 0.9f + 0.1f * (u / (1f + u));
                    data[i] = x < 0f ? -ax : ax;
                }
            }
            level = played > 0 ? (float)System.Math.Sqrt(sumSq / played) : 0f;
        }

        // ---------- Ana iş parçacığı ----------

        void Update()
        {
            FeedStream();
            AdaptTarget();

            if (Time.time >= nextCheck)
            {
                nextCheck = Time.time + CheckInterval;
                var listener = FindListener();
                bool blocked = listener != null && IsOccluded(listener);
                float dist = listener != null ? Vector3.Distance(listener.transform.position, transform.position) : 0f;
                float baseCut = blocked ? occludedCutoff : openCutoff;
                targetCutoff = Mathf.Min(baseCut, AirCutoff(dist));
                targetMul = blocked ? occludedVolume : 1f;
            }

            float t = 1f - Mathf.Exp(-10f * Time.deltaTime);
            // Kesim frekansını logaritmik uzayda yumuşat
            float lc = Mathf.Lerp(Mathf.Log(Mathf.Max(10f, curCutoff)), Mathf.Log(Mathf.Max(10f, targetCutoff)), t);
            curCutoff = Mathf.Exp(lc);
            curMul = Mathf.Lerp(curMul, targetMul, t);
            lowPass.cutoffFrequency = Mathf.Clamp(curCutoff, 10f, 22000f);
            source.volume = Mathf.Clamp01(volume) * curMul;
        }

        // 8 m'ye kadar açık, maxDistance'ta ~5 kHz (logaritmik)
        float AirCutoff(float dist)
        {
            float t = Mathf.Clamp01((dist - AirNearDistance) / Mathf.Max(0.01f, maxDistance - AirNearDistance));
            return openCutoff * Mathf.Pow(AirFarCutoff / openCutoff, t);
        }

        // Jitter hedefini uyarla: boşalmada artır, sakin akışta yavaşça azalt
        void AdaptTarget()
        {
            int ur;
            bool playing;
            lock (sync)
            {
                ur = underruns;
                underruns = 0;
                playing = state == PlayState.Playing;
            }
            if (ur > 0)
            {
                calmTimer = 0f;
                lock (sync) targetSeconds = Mathf.Min(TargetMax, targetSeconds + TargetStep * Mathf.Min(ur, 3));
            }
            else if (playing)
            {
                calmTimer += Time.unscaledDeltaTime;
                if (calmTimer >= DecayInterval)
                {
                    calmTimer = 0f;
                    lock (sync) targetSeconds = Mathf.Max(TargetMin, targetSeconds - TargetDecay);
                }
            }
        }

        bool IsOccluded(AudioListener listener)
        {
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
