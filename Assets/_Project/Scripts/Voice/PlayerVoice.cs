using System;
using System.Collections.Generic;
using MimicGame.Mimic;
using MimicGame.Network;
using MimicGame.Player;
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

        /// <summary>Ağ paketinde güvenli yük sınırı (bayt).</summary>
        const int MaxPayload = 1000;
        const int MaxIncoming = 65536;
        const float UtteranceGap = 0.45f;   // bu kadar sessizlikte yeni konuşma
        const float UtteranceMax = 5f;      // sürekli akışta zorla bölme

        public VoicePlayer voicePlayer;

        public bool IsMuted { get; private set; }

        VoiceCapture capture;
        PlayerInputReader input;
        PlayerHealth health;
        readonly List<VoicePacket> packets = new List<VoicePacket>(8);

        // Alıcı tarafı tamponu
        float[] decodeFloats = new float[4096];

        // Konuşma (utterance) numaralandırma: sahip tarafı atar
        int utteranceId;
        float lastSendTime = -100f;
        float utteranceStart;

        bool warnedNoCapture;

        void Awake()
        {
            input = GetComponent<PlayerInputReader>();
            health = GetComponent<PlayerHealth>();
        }

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) return;
            Local = this;
            IsMuted = false;
            utteranceId = UnityEngine.Random.Range(1, 1 << 20);
            lastSendTime = -100f;
            try
            {
                capture = new VoiceCapture(SteamBootstrap.Available);
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
            capture.Shutdown();
            capture = null;
        }

        void Update()
        {
            // Sunucu: konuşma bitiş zamanlayıcısı (kare başına bir kez işler)
            if (IsSpawned && IsServer) SpeechTracker.Tick();
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
            // Host'un kendi sesi: sunucu konuşma sinyali
            if (IsServer) SpeechTracker.Mark(OwnerClientId, transform.position);

            // Konuşma numarası: >= 0.45 sn ara veya 5 sn'yi aşan akışta yenisi
            float now = Time.unscaledTime;
            if (now - lastSendTime >= UtteranceGap || now - utteranceStart > UtteranceMax)
            {
                utteranceId++;
                utteranceStart = now;
            }
            lastSendTime = now;

            // Kendi sesimizi de kliplere kaydet (diğerleri Receive'de kaydeder)
            ClipRecorder.OnPacket(OwnerClientId, utteranceId, p.codec, data);

            // Büyükse güvenilir kanaldan gönder (Steam paketi bölünemez)
            if (data.Length <= MaxPayload) VoiceRpc(p.codec, utteranceId, data);
            else VoiceReliableRpc(p.codec, utteranceId, data);
        }

        [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Owner)]
        void VoiceRpc(byte codec, int utt, byte[] data) => Receive(codec, utt, data);

        [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Reliable, InvokePermission = RpcInvokePermission.Owner)]
        void VoiceReliableRpc(byte codec, int utt, byte[] data) => Receive(codec, utt, data);

        void Receive(byte codec, int utterance, byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > MaxIncoming) return;
            if (IsServer && !IsOwner)
                SpeechTracker.Mark(OwnerClientId, transform.position);
            if (IsOwner) return;

            // Klip kaydı: canlı sesle aynı paketlerden
            ClipRecorder.OnPacket(OwnerClientId, utterance, codec, data);

            // Ölüler yalnızca ölülere duyulur (klip kaydı yukarıda yapıldı: taklitçi kullanabilir)
            bool speakerDead = health != null && health.Dead;
            var me = PlayerHealth.Local;
            bool meAlive = me == null || !me.Dead;
            if (speakerDead && meAlive) return;

            if (voicePlayer == null) return;
            if (!VoiceCodec.Decode(codec, data, data.Length, ref decodeFloats, out int n, out int rate)) return;
            voicePlayer.PushSamples(decodeFloats, n, rate);
        }
    }
}
