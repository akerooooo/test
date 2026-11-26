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
        const float VadHangover = 0.5f;
        const float PreRollSeconds = 0.2f;
        const int MaxPacketBytes = 960; // 480 örnek

        public bool Transmitting { get; private set; }

        bool useSteam;

        // Mikrofon
        AudioClip micClip;
        int micRate;
        int micLastPos;
        float vadHang;
        bool vadActive;
        float[] micBuf = new float[2048];
        float[] micOut = new float[2048];
        float[] readTmp = new float[1024];
        bool micFailed;

        // Yeniden örnekleme durumu
        double resPos;
        float resPrev;
        bool haveCarry;
        float carry;

        // Ön kayıt (son 200 ms, gönderim hızında)
        readonly List<float> preRoll = new List<float>(MicRate / 4);

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
            for (int guard = 0; guard < 8 && SteamUser.HasVoiceData; guard++)
            {
                byte[] bytes = SteamUser.ReadVoiceDataBytes();
                if (bytes == null || bytes.Length == 0) break;
                output.Add(new VoicePacket { codec = CodecSteam, data = bytes });
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
            micRate = PreferredCaptureRate;
            if (maxF > 0) micRate = Mathf.Clamp(PreferredCaptureRate, Mathf.Max(1, minF), maxF);

            micClip = Microphone.Start(null, true, 2, micRate);
            if (micClip == null) { micFailed = true; Debug.LogWarning("[Ses] Mikrofon başlatılamadı."); return; }
            micLastPos = 0;
            vadHang = 0f;
            vadActive = false;
            resPos = 0.0;
            resPrev = 0f;
            haveCarry = false;
            preRoll.Clear();
            Debug.Log("[Ses] Mikrofon yedeği: yakalama " + micClip.frequency + " Hz, gönderim " + MicRate + " Hz.");
        }

        void StopMic()
        {
            if (micClip != null)
            {
                Microphone.End(null);
                micClip = null;
            }
            vadActive = false;
            preRoll.Clear();
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
            int n = pos >= micLastPos ? pos - micLastPos : total - micLastPos + pos;
            if (n <= 0) return;
            if (micBuf.Length < n) micBuf = new float[n];

            // Sarmayı iki parça halinde oku
            int first = pos >= micLastPos ? n : total - micLastPos;
            ReadMic(micLastPos, first, 0);
            if (first < n) ReadMic(0, n - first, first);
            micLastPos = pos;

            int m = ResampleToSendRate(micBuf, n, micClip.frequency);
            if (m <= 0) return;

            // Ses algılama kapısı
            float sum = 0f;
            for (int i = 0; i < m; i++) sum += micOut[i] * micOut[i];
            float rms = Mathf.Sqrt(sum / m);
            float blockSec = (float)m / MicRate;
            bool wasActive = vadActive;
            if (rms > VadThreshold) { vadHang = VadHangover; vadActive = true; }
            else if (vadActive)
            {
                vadHang -= blockSec;
                if (vadHang <= 0f) vadActive = false;
            }

            if (!vadActive)
            {
                // Sessiz: ön kayıt geçmişini güncel tut
                AppendPreRoll(micOut, m);
                return;
            }

            // Konuşma başlıyorsa önce ön kaydı gönder (kelime başı kesilmesin)
            if (!wasActive && preRoll.Count > 0)
            {
                var pre = preRoll.ToArray();
                preRoll.Clear();
                EmitPcm(pre, pre.Length, output);
            }
            else preRoll.Clear();
            EmitPcm(micOut, m, output);
        }

        void AppendPreRoll(float[] src, int count)
        {
            int max = Mathf.RoundToInt(MicRate * PreRollSeconds);
            for (int i = 0; i < count; i++) preRoll.Add(src[i]);
            if (preRoll.Count > max) preRoll.RemoveRange(0, preRoll.Count - max);
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

        void ReadMic(int offset, int count, int dest)
        {
            if (count <= 0) return;
            if (readTmp.Length != count) readTmp = new float[count];
            micClip.GetData(readTmp, offset);
            Array.Copy(readTmp, 0, micBuf, dest, count);
        }

        // Girdiyi (inRate) gönderim hızına çevirir; sonuç micOut'ta. 2:1 ise ikili ortalama, değilse doğrusal.
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
                // Ortalama alarak yarıya indir (basit düşük geçirgen etkisi)
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

            // Doğrusal (bloklar arası süreklilikle)
            double step = (double)inRate / MicRate;
            int maxOut = (int)(n / step) + 2;
            if (micOut.Length < maxOut) micOut = new float[maxOut];
            int cnt = 0;
            double p = resPos;
            while (p < n - 1 && cnt < micOut.Length)
            {
                int i = (int)Math.Floor(p);
                float a = i < 0 ? resPrev : src[i];
                float b = src[i + 1];
                micOut[cnt++] = a + (b - a) * (float)(p - i);
                p += step;
            }
            resPos = p - n;
            resPrev = src[n - 1];
            return cnt;
        }
    }
}
