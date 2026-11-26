using System;
using System.Collections.Generic;
using System.IO;
using MimicGame.Network;
using Steamworks;
using UnityEngine;

namespace MimicGame.Voice
{
    /// <summary>Gönderilecek kodlanmış ses paketi. codec 0: PCM16 LE 16 kHz, codec 1: Steam sıkıştırması.</summary>
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
        public const int MicRate = 16000;

        const float VadThreshold = 0.01f;
        const float VadHangover = 0.3f;

        /// <summary>Yerel oyuncunun çözülmüş PCM'i (örnekler, adet, hız). Dizi sadece çağrı süresince geçerli.</summary>
        public event Action<float[], int, int> LocalPcm;

        public bool Transmitting { get; private set; }

        bool useSteam;

        // Steam
        readonly MemoryStream decodeStream = new MemoryStream(8192);
        float[] decodeFloats = new float[4096];

        // Mikrofon
        AudioClip micClip;
        int micLastPos;
        float vadHang;
        float[] micBuf = new float[1024];
        float[] micResampled = new float[1024];
        bool micFailed;

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
            LocalPcm = null;
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
                if (LocalPcm != null) DecodeLocal(bytes);
            }
        }

        void DecodeLocal(byte[] bytes)
        {
            decodeStream.SetLength(0);
            int written = SteamUser.DecompressVoice(bytes, decodeStream);
            if (written <= 0) return;
            int n = written / 2;
            if (decodeFloats.Length < n) decodeFloats = new float[n];
            var buf = decodeStream.GetBuffer();
            for (int i = 0; i < n; i++)
                decodeFloats[i] = (short)(buf[i * 2] | (buf[i * 2 + 1] << 8)) / 32768f;
            LocalPcm?.Invoke(decodeFloats, n, (int)SteamUser.SampleRate);
        }

        // ---------- Mikrofon yedeği ----------

        void StartMic()
        {
            if (micFailed) return;
            if (Microphone.devices.Length == 0) { micFailed = true; Debug.LogWarning("Mikrofon bulunamadı."); return; }
            micClip = Microphone.Start(null, true, 1, MicRate);
            micLastPos = 0;
            vadHang = 0f;
        }

        void StopMic()
        {
            if (micClip != null)
            {
                Microphone.End(null);
                micClip = null;
            }
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

            // Basit ses algılama kapısı
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += micBuf[i] * micBuf[i];
            float rms = Mathf.Sqrt(sum / n);
            if (rms > VadThreshold) vadHang = VadHangover;
            else vadHang -= (float)n / micClip.frequency;
            if (vadHang <= 0f) return;

            float[] pcm = micBuf;
            int pcmCount = n;
            int rate = micClip.frequency;
            if (rate != MicRate) { pcmCount = Resample(micBuf, n, rate, MicRate); pcm = micResampled; }

            var bytes = new byte[pcmCount * 2];
            for (int i = 0; i < pcmCount; i++)
            {
                short s = (short)Mathf.RoundToInt(Mathf.Clamp(pcm[i], -1f, 1f) * 32767f);
                bytes[i * 2] = (byte)(s & 0xFF);
                bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            output.Add(new VoicePacket { codec = CodecPcm16, data = bytes });
            LocalPcm?.Invoke(pcm, pcmCount, MicRate);
        }

        float[] readTmp = new float[1024];

        void ReadMic(int offset, int count, int dest)
        {
            if (count <= 0) return;
            if (readTmp.Length != count) readTmp = new float[count];
            micClip.GetData(readTmp, offset);
            Array.Copy(readTmp, 0, micBuf, dest, count);
        }

        int Resample(float[] src, int n, int inRate, int outRate)
        {
            double step = (double)inRate / outRate;
            int outCount = Mathf.Max(1, (int)(n / step));
            if (micResampled.Length < outCount) micResampled = new float[outCount];
            for (int i = 0; i < outCount; i++)
            {
                double p = i * step;
                int a = (int)p;
                int b = Mathf.Min(a + 1, n - 1);
                micResampled[i] = src[a] + (src[b] - src[a]) * (float)(p - a);
            }
            return outCount;
        }
    }
}
