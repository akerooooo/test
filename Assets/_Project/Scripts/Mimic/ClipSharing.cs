using System.Collections.Generic;
using MimicGame.Voice;
using Unity.Netcode;
using UnityEngine;

namespace MimicGame.Mimic
{
    /// <summary>
    /// Oyuncu prefabında: sahibin konuşmasını kısa kliplere ayırır, kütüphaneye ekler ve
    /// µ-law ile diğer oyunculara yollar. Alıcılar parçaları birleştirip kütüphaneye ekler.
    /// </summary>
    public class ClipSharing : NetworkBehaviour
    {
        const string PrefKey = "AllowMimicry";
        public const int ClipRate = 16000;

        // Kayıt ayarları
        const float StartRms = 0.015f;
        const float EndSilence = 0.45f;
        const float MinSeconds = 0.4f;
        const float MaxSeconds = 6f;
        const float TrailKeep = 0.1f;

        // Ağ ayarları
        const int ChunkBytes = 900;
        const int ChunksPerFrame = 8;
        const int MaxChunkCount = 200;
        const float AssembleTimeout = 10f;

        static int allowCache = -1;

        /// <summary>Sesimin taklit edilmesine izin ver (menüdeki gizlilik ayarı).</summary>
        public static bool AllowMimicry
        {
            get
            {
                if (allowCache < 0) allowCache = PlayerPrefs.GetInt(PrefKey, 1) != 0 ? 1 : 0;
                return allowCache != 0;
            }
            set
            {
                allowCache = value ? 1 : 0;
                PlayerPrefs.SetInt(PrefKey, allowCache);
                PlayerPrefs.Save();
            }
        }

        // ---- Kaydedici durumu (yalnızca sahip) ----
        readonly List<float> rec = new List<float>(ClipRate * 7);
        float[] block = new float[1024];
        double resPos = -1.0;
        float resPrev;
        bool recording, overflow;
        int lastLoudEnd;
        float silence;
        float lastBlockTime;
        int nextId = 1;

        // ---- Gönderim kuyruğu ----
        struct OutChunk { public int id, index, count; public byte[] data; }
        readonly Queue<OutChunk> outQueue = new Queue<OutChunk>();

        // ---- Alıcı birleştirme ----
        class Assembly
        {
            public byte[][] parts;
            public int received;
            public float startTime;
        }
        readonly Dictionary<int, Assembly> assemblies = new Dictionary<int, Assembly>();
        readonly List<int> staleKeys = new List<int>();

        public override void OnNetworkSpawn()
        {
            if (IsOwner) PlayerVoice.LocalPcmCaptured += OnPcm;
        }

        public override void OnNetworkDespawn()
        {
            PlayerVoice.LocalPcmCaptured -= OnPcm;
            outQueue.Clear();
            assemblies.Clear();
            // Yerel oyuncu çıkınca (bağlantı bitti) kütüphaneyi temizle
            if (IsOwner) VoiceClipLibrary.Clear();
        }

        void OnDisable() => PlayerVoice.LocalPcmCaptured -= OnPcm;

        void Update()
        {
            if (!IsSpawned) return;

            if (IsOwner)
            {
                // Paket gelmeyince (VAD kapısı) sessizlik süresi duvar saatiyle ölçülür
                if (recording && Time.unscaledTime - lastBlockTime > EndSilence) FinishClip();

                for (int i = 0; i < ChunksPerFrame && outQueue.Count > 0; i++)
                {
                    var c = outQueue.Dequeue();
                    ClipChunkRpc(c.id, c.index, c.count, c.data);
                }
            }
            else if (assemblies.Count > 0)
            {
                staleKeys.Clear();
                foreach (var kv in assemblies)
                    if (Time.unscaledTime - kv.Value.startTime > AssembleTimeout) staleKeys.Add(kv.Key);
                for (int i = 0; i < staleKeys.Count; i++) assemblies.Remove(staleKeys[i]);
            }
        }

        // ---------- Kayıt ----------

        void OnPcm(float[] samples, int count, int rate)
        {
            if (!IsSpawned || !IsOwner) return;
            if (!AllowMimicry)
            {
                ResetRecorder();
                return;
            }
            if (samples == null || count <= 0 || rate <= 0) return;

            float now = Time.unscaledTime;
            if (recording && now - lastBlockTime > EndSilence) FinishClip();
            lastBlockTime = now;

            // 16 kHz'e doğrusal yeniden örnekleme (bloklar arası süreklilikle)
            double step = (double)rate / ClipRate;
            int maxOut = (int)(count / step) + 2;
            if (block.Length < maxOut) block = new float[maxOut];
            int n = 0;
            double p = resPos;
            while (p < count - 1 && n < block.Length)
            {
                int i = (int)System.Math.Floor(p);
                float a = i < 0 ? resPrev : samples[i];
                float b = samples[i + 1];
                block[n++] = a + (b - a) * (float)(p - i);
                p += step;
            }
            resPos = p - count;
            resPrev = samples[count - 1];
            if (n <= 0) return;

            float sum = 0f;
            for (int i = 0; i < n; i++) sum += block[i] * block[i];
            float rms = Mathf.Sqrt(sum / n);
            float dur = (float)n / ClipRate;
            bool loud = rms > StartRms;

            if (!recording)
            {
                if (!loud) { overflow = false; return; }
                if (overflow) return; // uzun konuşmanın artığı; sessizliği bekle
                recording = true;
                rec.Clear();
                silence = 0f;
                lastLoudEnd = 0;
            }

            for (int i = 0; i < n; i++) rec.Add(block[i]);
            if (loud) { silence = 0f; lastLoudEnd = rec.Count; }
            else silence += dur;

            if (rec.Count > MaxSeconds * ClipRate)
            {
                // Çok uzun: at, sessizlik gelene kadar yeni kayıt başlatma
                ResetRecorder();
                overflow = true;
                return;
            }
            if (silence > EndSilence) FinishClip();
        }

        void ResetRecorder()
        {
            recording = false;
            overflow = false;
            rec.Clear();
            silence = 0f;
            lastLoudEnd = 0;
        }

        void FinishClip()
        {
            if (!recording) return;
            recording = false;

            // Sondaki sessizliği ~0.1 sn'ye kırp
            int end = Mathf.Min(rec.Count, lastLoudEnd + (int)(TrailKeep * ClipRate));
            float seconds = (float)end / ClipRate;
            if (seconds >= MinSeconds && seconds <= MaxSeconds) Publish(end);
            rec.Clear();
            silence = 0f;
            lastLoudEnd = 0;
        }

        void Publish(int length)
        {
            var samples = new float[length];
            rec.CopyTo(0, samples, 0, length);

            int id = nextId++;
            float duration = (float)length / ClipRate;
            VoiceClipLibrary.Add(new VoiceClip
            {
                speakerClientId = OwnerClientId,
                id = id,
                samples = samples,
                sampleRate = ClipRate,
                duration = duration,
                tag = duration < VoiceClipLibrary.ShortLimit ? ClipTag.Short : ClipTag.General,
            });

            // µ-law kodla ve parçalara böl
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = MuLaw.Encode(samples[i]);
            int chunkCount = (length + ChunkBytes - 1) / ChunkBytes;
            for (int c = 0; c < chunkCount; c++)
            {
                int off = c * ChunkBytes;
                int len = Mathf.Min(ChunkBytes, length - off);
                var part = new byte[len];
                System.Buffer.BlockCopy(bytes, off, part, 0, len);
                outQueue.Enqueue(new OutChunk { id = id, index = c, count = chunkCount, data = part });
            }
        }

        // ---------- Alım ----------

        [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Reliable, InvokePermission = RpcInvokePermission.Owner)]
        void ClipChunkRpc(int clipId, int chunkIndex, int chunkCount, byte[] data)
        {
            if (IsOwner || data == null) return;
            if (chunkCount <= 0 || chunkCount > MaxChunkCount) return;
            if (chunkIndex < 0 || chunkIndex >= chunkCount) return;
            if (data.Length == 0 || data.Length > ChunkBytes + 100) return;

            if (!assemblies.TryGetValue(clipId, out var asm) || asm.parts.Length != chunkCount)
            {
                asm = new Assembly { parts = new byte[chunkCount][], startTime = Time.unscaledTime };
                assemblies[clipId] = asm;
            }
            if (asm.parts[chunkIndex] != null) return;
            asm.parts[chunkIndex] = data;
            asm.received++;
            if (asm.received < chunkCount) return;

            assemblies.Remove(clipId);
            int total = 0;
            for (int i = 0; i < chunkCount; i++) total += asm.parts[i].Length;
            var samples = new float[total];
            int pos = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                var part = asm.parts[i];
                for (int j = 0; j < part.Length; j++) samples[pos++] = MuLaw.Decode(part[j]);
            }
            float duration = (float)total / ClipRate;
            VoiceClipLibrary.Add(new VoiceClip
            {
                speakerClientId = OwnerClientId,
                id = clipId,
                samples = samples,
                sampleRate = ClipRate,
                duration = duration,
                tag = duration < VoiceClipLibrary.ShortLimit ? ClipTag.Short : ClipTag.General,
            });
        }
    }
}
