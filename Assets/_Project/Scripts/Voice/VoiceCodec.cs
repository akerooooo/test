using System.IO;
using MimicGame.Network;
using Steamworks;
using UnityEngine;

namespace MimicGame.Voice
{
    /// <summary>
    /// Ses paketlerini PCM'e çözen ortak kod. Canlı alım ve taklitçi klip oynatma aynı yolu kullanır
    /// (böylece kalite birebir aynı olur). Yalnızca ana iş parçacığından çağrılmalı.
    /// </summary>
    public static class VoiceCodec
    {
        static readonly MemoryStream stream = new MemoryStream(65536);

        /// <summary>
        /// Paketi çözer. pcm gerekirse büyütülür; count geçerli örnek sayısıdır (mono, -1..1).
        /// Çözülemezse false döner.
        /// </summary>
        public static bool Decode(byte codec, byte[] data, int length, ref float[] pcm, out int count, out int sampleRate)
        {
            count = 0;
            sampleRate = 0;
            if (data == null || length <= 0 || length > data.Length) return false;

            byte[] src;
            int bytes;
            if (codec == VoiceCapture.CodecPcm16)
            {
                src = data;
                bytes = length & ~1;
                sampleRate = VoiceCapture.MicRate;
            }
            else if (codec == VoiceCapture.CodecSteam)
            {
                if (!SteamBootstrap.Available) return false; // Steam yoksa çözemeyiz
                byte[] input = data;
                if (length != data.Length)
                {
                    input = new byte[length];
                    System.Buffer.BlockCopy(data, 0, input, 0, length);
                }
                int written;
                try
                {
                    stream.SetLength(0);
                    written = SteamUser.DecompressVoice(input, stream);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("[Ses] Steam çözme hatası: " + e.Message);
                    return false;
                }
                if (written <= 0) return false;
                src = stream.GetBuffer();
                bytes = written & ~1;
                sampleRate = (int)SteamUser.SampleRate;
            }
            else return false;

            int n = bytes / 2;
            if (n <= 0 || sampleRate <= 0) return false;
            if (pcm == null || pcm.Length < n) pcm = new float[Mathf.Max(n, 4096)];
            for (int i = 0; i < n; i++)
                pcm[i] = (short)(src[i * 2] | (src[i * 2 + 1] << 8)) / 32768f;
            count = n;
            return true;
        }
    }
}
