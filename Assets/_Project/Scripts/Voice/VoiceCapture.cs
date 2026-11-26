using System;
using System.Collections.Generic;
using MimicGame.Network;
using Steamworks;
using UnityEngine;

namespace MimicGame.Voice
{
    /// <summary>Gönderilecek kodlanmış ses paketi. codec 0: PCM16 LE (MicRate), codec 1: Steam sıkıştırması.</summary>
    public struct VoicePacket
    {
        public byte codec;
        public byte[] data;
    }

    /// <summary>
    /// Mikrofon yakalama soyutlaması: Steam (tercih) veya Unity Microphone yedeği.
    /// Sahip tarafında PlayerVoice her karede Poll çağırır.
    /// </summary>
    public class VoiceCapture
    {
        public const byte CodecPcm16 = 0;
        public const byte CodecSteam = 1;
        /// <summary>Yedek (mikrofon) yolunun GÖNDERİM hızı; yakalama bundan yüksek olabilir.</summary>
        public const int MicRate = 24000;

        const int PreferredCaptureRate = 48000;
        const float VadThreshold = 0.008f;
        const float VadHangover = 0.6f;      // eşik altında bu kadar sürerse kapı kapanır
        const float PreRollSeconds = 0.2f;
        const float GateFadeSeconds = 0.005f; // kapı açılış/kapanışında yumuşak giriş/çıkış
        const float ChunkSeconds = 0.01f;     // 10 ms'lik bloklarla işle
        const int MaxPacketBytes = 960;       // 480 örnek

        public bool Transmitting { get; private set; }

        bool useSteam;

        // Mikrofon
        AudioClip micClip;
        int micRate;          // clip.frequency (cihazın gerçek hızı)
        int micLastPos;
        int chunkSize;
        float[] chunkBuf = new float[480];
        float[] micOut = new float[1024];
        float[] filtBuf = new float[1024];
        float[] emitBuf = new float[4096];
        int emitCount;
        bool micFailed;

        // Kapı
        bool gateOpen;
        float belowSeconds;

        // Yeniden örnekleme durumu
        double resPos;
        float resPrev;
        bool haveCarry;
        float carry;
        float fx1, fx2; // ön süzgeç geçmişi (x[i-1], x[i-2])

        // Ön kayıt (son 200 ms, gönderim hızında; dairesel)
        readonly float[] preRoll = new float[Mathf.RoundToInt(MicRate * PreRollSeconds)];
        int preHead, preCount;

        public VoiceCapture(bool preferSteam)
        {
            useSteam = preferSteam && SteamBootstrap.Available;
        }

        /// <summary>Kaydı aç/kapat. Kapalıyken hiçbir şey yakalanmaz.</summary>
        public void SetTransmitting(bool on)
        {
            if (on == Transmitting) return;
            Transmitting = on;
            if (useSteam)
            {
                try { SteamUser.VoiceRecord = on; }
                catch (Exception e) { Debug.LogWarning(e); }
            }
            else if (on) StartMic();
            else StopMic();
        }

        /// <summary>Tamamen kapat (oyundan çıkış / yok olma).</summary>
        public void Shutdown()
        {
            SetTransmitting(false);
            // Yalnızca Steam'i gerçekten bırak
            if (useSteam && SteamBootstrap.Available && SteamUser.VoiceRecord)
            {
                try { SteamUser.VoiceRecord = false; } catch (Exception e) { Debug.LogWarning(e); }
            }
        }

        /// <summary>Bu karede biriken paketleri listeye ekler.</summary>
        public void Poll(List<VoicePacket> output)
        {
            if (!Transmitting) return;
            if (useSteam) PollSteam(output);
            else PollMic(output);
        }

        // ---------- Steam ----------

        void PollSteam(List<VoicePacket> output)
        {
            if (!SteamBootstrap.Available) return;
            for (int guard = 0; guard < 32 && SteamUser.HasVoiceData; guard++)
            {
                byte[] bytes = SteamUser.ReadVoiceDataBytes();
                if (bytes == null || bytes.Length == 0) break;
                // Steam'in iç tamponu yeniden kullanılabilir: hemen kendi kopyamıza al
                var copy = new byte[bytes.Length];
                Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
                output.Add(new VoicePacket { codec = CodecSteam, data = copy });
            }
        }

        // ---------- Mikrofon yedeği ----------

        void StartMic()
        {
            if (micFailed) return;
            if (Microphone.devices.Length == 0) { micFailed = true; Debug.LogWarning("[Ses] Mikrofon bulunamadı."); return; }

            // Mümkünse 48 kHz yakala (aşağı örneklemede kalite kazancı)
            int minF, maxF;
            Microphone.GetDeviceCaps(null, out minF, out maxF);
            int want = PreferredCaptureRate;
            if (maxF > 0) want = Mathf.Clamp(PreferredCaptureRate, Mathf.Max(1, minF), maxF);

            micClip = Microphone.Start(null, true, 2, want);
            if (micClip == null) { micFailed = true; Debug.LogWarning("[Ses] Mikrofon başlatılamadı."); return; }

            micRate = micClip.frequency; // gerçekte dönen hız
            chunkSize = Mathf.Max(64, micRate / 100);
            if (chunkBuf.Length != chunkSize) chunkBuf = new float[chunkSize];
            micLastPos = 0;
            gateOpen = false;
            belowSeconds = 0f;
            resPos = 0.0;
            resPrev = 0f;
            haveCarry = false;
            fx1 = fx2 = 0f;
            preHead = 0;
            preCount = 0;
            Debug.Log("[Ses] Mikrofon yedeği: yakalama " + micRate + " Hz, gönderim " + MicRate + " Hz.");
        }

        void StopMic()
        {
            if (micClip != null)
            {
                Microphone.End(null);
                micClip = null;
            }
            gateOpen = false;
            preCount = 0;
        }

        void PollMic(List<VoicePacket> output)
        {
            if (micClip == null)
            {
                if (!micFailed) StartMic();
                if (micClip == null) return;
            }
            int pos = Microphone.GetPosition(null);
            if (pos < 0) return;
            int total = micClip.samples;
            if (pos >= total) pos = 0;
            int avail = pos >= micLastPos ? pos - micLastPos : total - micLastPos + pos;

            // Çok geç kalındıysa (eski veri ezilmiş olabilir) son 100 ms'e atla
            if (avail > total - chunkSize * 2)
            {
                int keep = micRate / 10;
                micLastPos = ((pos - keep) % total + total) % total;
                avail = keep;
            }

            emitCount = 0;
            while (avail >= chunkSize)
            {
                ReadMic(micLastPos, chunkSize, total);
                micLastPos = (micLastPos + chunkSize) % total;
                avail -= chunkSize;
                ProcessChunk(chunkBuf, chunkSize);
            }
            if (emitCount > 0) EmitPcm(emitBuf, emitCount, output);
        }

        // Sarma noktasını açıkça iki parçada oku (GetData sarma davranışına güvenme)
        void ReadMic(int offset, int count, int total)
        {
            if (offset + count <= total)
            {
                micClip.GetData(chunkBuf, offset);
                return;
            }
            int first = total - offset;
            var a = new float[first];
            var b = new float[count - first];
            micClip.GetData(a, offset);
            micClip.GetData(b, 0);
            Array.Copy(a, 0, chunkBuf, 0, first);
            Array.Copy(b, 0, chunkBuf, first, count - first);
        }

        // Bir 10 ms'lik bloğu gönderim hızına çevir, kapıdan geçir
        void ProcessChunk(float[] src, int n)
        {
            int m = ResampleToSendRate(src, n, micRate);
            if (m <= 0) return;

            float sum = 0f;
            for (int i = 0; i < m; i++) sum += micOut[i] * micOut[i];
            float rms = Mathf.Sqrt(sum / m);
            float blockSec = (float)m / MicRate;
            int fadeLen = Mathf.Max(1, Mathf.RoundToInt(MicRate * GateFadeSeconds));

            if (!gateOpen)
            {
                if (rms > VadThreshold)
                {
                    // Kapı açılıyor: önce ön kaydı (yumuşak girişle), sonra bu bloğu ekle
                    gateOpen = true;
                    belowSeconds = 0f;
                    int startAt = emitCount;
                    for (int i = 0; i < preCount; i++)
                        AddEmit(preRoll[(preHead + i) % preRoll.Length]);
                    preCount = 0;
                    for (int i = 0; i < m; i++) AddEmit(micOut[i]);
                    int fi = Mathf.Min(fadeLen, emitCount - startAt);
                    for (int i = 0; i < fi; i++) emitBuf[startAt + i] *= (float)i / fi;
                }
                else AppendPreRoll(micOut, m);
                return;
            }

            // Kapı açık
            if (rms > VadThreshold) belowSeconds = 0f;
            else belowSeconds += blockSec;

            for (int i = 0; i < m; i++) AddEmit(micOut[i]);

            if (belowSeconds >= VadHangover)
            {
                // Kapanış: son bloğun sonunu yumuşakça sustur
                int fo = Mathf.Min(fadeLen, m);
                for (int i = 0; i < fo; i++) emitBuf[emitCount - 1 - i] *= (float)i / fo;
                gateOpen = false;
                belowSeconds = 0f;
                preCount = 0;
            }
        }

        void AddEmit(float v)
        {
            if (emitCount >= emitBuf.Length) Array.Resize(ref emitBuf, emitBuf.Length * 2);
            emitBuf[emitCount++] = v;
        }

        void AppendPreRoll(float[] src, int count)
        {
            int cap = preRoll.Length;
            for (int i = 0; i < count; i++)
            {
                if (preCount < cap) preRoll[(preHead + preCount++) % cap] = src[i];
                else { preRoll[preHead] = src[i]; preHead = (preHead + 1) % cap; }
            }
        }

        // PCM16'ya çevirip en fazla 960 baytlık (tam örnekli) paketlere böler
        void EmitPcm(float[] pcm, int count, List<VoicePacket> output)
        {
            const int SamplesPerPacket = MaxPacketBytes / 2;
            for (int off = 0; off < count; off += SamplesPerPacket)
            {
                int len = Mathf.Min(SamplesPerPacket, count - off);
                var bytes = new byte[len * 2];
                for (int i = 0; i < len; i++)
                {
                    short s = (short)Mathf.RoundToInt(Mathf.Clamp(pcm[off + i], -1f, 1f) * 32767f);
                    bytes[i * 2] = (byte)(s & 0xFF);
                    bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
                output.Add(new VoicePacket { codec = CodecPcm16, data = bytes });
            }
        }

        // Girdiyi (inRate) gönderim hızına çevirir; sonuç micOut'ta. 2:1 ise ikili ortalama,
        // değilse hafif ön süzgeç + doğrusal (bloklar arası süreklilikle).
        int ResampleToSendRate(float[] src, int n, int inRate)
        {
            if (inRate == MicRate)
            {
                if (micOut.Length < n) micOut = new float[n];
                Array.Copy(src, 0, micOut, 0, n);
                return n;
            }

            if (inRate == MicRate * 2)
            {
                int idx = 0;
                int outMax = (n + 1) / 2 + 1;
                if (micOut.Length < outMax) micOut = new float[outMax];
                int o = 0;
                if (haveCarry && n > 0)
                {
                    micOut[o++] = 0.5f * (carry + src[0]);
                    idx = 1;
                    haveCarry = false;
                }
                for (; idx + 1 < n; idx += 2) micOut[o++] = 0.5f * (src[idx] + src[idx + 1]);
                if (idx < n) { carry = src[idx]; haveCarry = true; }
                return o;
            }

            // Aşağı örnekleme ise [1 2 1]/4 ön süzgeç (örtüşmeyi azaltır)
            float[] x = src;
            if (inRate > MicRate)
            {
                if (filtBuf.Length < n) filtBuf = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float v = src[i];
                    filtBuf[i] = 0.25f * v + 0.5f * fx1 + 0.25f * fx2;
                    fx2 = fx1;
                    fx1 = v;
                }
                x = filtBuf;
            }

            double step = (double)inRate / MicRate;
            int maxOut = (int)(n / step) + 2;
            if (micOut.Length < maxOut) micOut = new float[maxOut];
            int cnt = 0;
            double p = resPos;
            while (p < n - 1 && cnt < micOut.Length)
            {
                int i = (int)Math.Floor(p);
                float a = i < 0 ? resPrev : x[i];
                float b = x[i + 1];
                micOut[cnt++] = a + (b - a) * (float)(p - i);
                p += step;
            }
            resPos = p - n;
            resPrev = x[n - 1];
            return cnt;
        }
    }
}
