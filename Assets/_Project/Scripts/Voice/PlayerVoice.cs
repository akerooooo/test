using System;
using System.Collections.Generic;
using System.IO;
using MimicGame.Network;
using MimicGame.Player;
using Steamworks;
using Unity.Netcode;
using UnityEngine;

namespace MimicGame.Voice
{
    /// <summary>
    /// Oyuncu prefabının yakınlık ses sohbeti köprüsü. Sahip mikrofonu yakalar ve RPC ile yollar;
    /// diğerleri gelen paketi çözüp "Ses" çocuğundaki VoicePlayer'a verir.
    /// </summary>
    public class PlayerVoice : NetworkBehaviour
    {
        /// <summary>Yerel oyuncunun PlayerVoice'u (sahip doğunca atanır).</summary>
        public static PlayerVoice Local { get; private set; }

        /// <summary>Yerel oyuncunun çözülmüş sesi (örnekler, adet, hız). Dizi sadece çağrı süresince geçerli.</summary>
        public static event Action<float[], int, int> LocalPcmCaptured;

        /// <summary>Ağ paketinde güvenli yük sınırı (bayt).</summary>
        const int MaxPayload = 1000;
        const int MaxIncoming = 65536;

        public VoicePlayer voicePlayer;

        public bool IsMuted { get; private set; }

        VoiceCapture capture;
        PlayerInputReader input;
        readonly List<VoicePacket> packets = new List<VoicePacket>(8);

        // Alıcı tarafı tamponları
        readonly MemoryStream decodeStream = new MemoryStream(8192);
        float[] decodeFloats = new float[4096];

        bool warnedNoCapture;

        void Awake()
        {
            input = GetComponent<PlayerInputReader>();
        }

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) return;
            Local = this;
            IsMuted = false;
            try
            {
                capture = new VoiceCapture(SteamBootstrap.Available);
                capture.LocalPcm += ForwardLocalPcm;
            }
            catch (Exception e)
            {
                capture = null;
                Debug.LogWarning("[Ses] Yakalama başlatılırken hata: " + e);
            }
        }

        public override void OnNetworkDespawn()
        {
            StopCapture();
            if (Local == this) Local = null;
        }

        void OnDisable() => StopCapture();
        void OnApplicationQuit() => StopCapture();

        void StopCapture()
        {
            if (capture == null) return;
            capture.LocalPcm -= ForwardLocalPcm;
            capture.Shutdown();
            capture = null;
        }

        void ForwardLocalPcm(float[] samples, int count, int rate) => LocalPcmCaptured?.Invoke(samples, count, rate);

        void Update()
        {
            if (!IsSpawned || !IsOwner) return;

            // Sessize alma, yakalama başlatılamamış olsa da çalışsın
            if (input != null && input.MutePressed) IsMuted = !IsMuted;
            if (capture == null)
            {
                if (!warnedNoCapture)
                {
                    warnedNoCapture = true;
                    Debug.LogWarning("[Ses] Mikrofon yakalama başlatılamadı; bu oyuncunun sesi gönderilmiyor.");
                }
                return;
            }
            capture.SetTransmitting(!IsMuted);
            if (IsMuted) return;

            packets.Clear();
            capture.Poll(packets);
            for (int i = 0; i < packets.Count; i++) Send(packets[i]);
        }

        void Send(VoicePacket p)
        {
            var data = p.data;
            if (data == null || data.Length == 0) return;

            if (p.codec == VoiceCapture.CodecPcm16)
            {
                // Tam örnek sınırlarında parçala
                int chunk = MaxPayload & ~1;
                for (int off = 0; off < data.Length; off += chunk)
                {
                    int len = Math.Min(chunk, data.Length - off);
                    var part = new byte[len];
                    Buffer.BlockCopy(data, off, part, 0, len);
                    VoiceRpc(p.codec, part);
                }
                return;
            }

            // Steam paketi bölünemez: büyükse güvenilir kanaldan gönder
            if (data.Length <= MaxPayload) VoiceRpc(p.codec, data);
            else VoiceReliableRpc(p.codec, data);
        }

        [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Owner)]
        void VoiceRpc(byte codec, byte[] data) => Receive(codec, data);

        [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Reliable, InvokePermission = RpcInvokePermission.Owner)]
        void VoiceReliableRpc(byte codec, byte[] data) => Receive(codec, data);

        void Receive(byte codec, byte[] data)
        {
            if (IsOwner || voicePlayer == null || data == null) return;
            if (data.Length == 0 || data.Length > MaxIncoming) return;

            if (codec == VoiceCapture.CodecPcm16)
            {
                PushPcm16(data, data.Length, VoiceCapture.MicRate);
            }
            else if (codec == VoiceCapture.CodecSteam)
            {
                if (!SteamBootstrap.Available) return; // Steam yoksa çözemeyiz
                decodeStream.SetLength(0);
                int written = SteamUser.DecompressVoice(data, decodeStream);
                if (written <= 0) return;
                PushPcm16(decodeStream.GetBuffer(), written, (int)SteamUser.SampleRate);
            }
        }

        void PushPcm16(byte[] buf, int byteCount, int rate)
        {
            int n = byteCount / 2;
            if (n <= 0) return;
            if (decodeFloats.Length < n) decodeFloats = new float[n];
            for (int i = 0; i < n; i++)
                decodeFloats[i] = (short)(buf[i * 2] | (buf[i * 2 + 1] << 8)) / 32768f;
            voicePlayer.PushSamples(decodeFloats, n, rate);
        }
    }
}
