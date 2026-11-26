using System.Collections.Generic;
using MimicGame.Voice;
using Unity.Netcode;
using UnityEngine;

namespace MimicGame.Mimic
{
    /// <summary>
    /// Her makinede: gelen/giden ses paketlerini (konuşmacı, konuşma no) ile gruplayıp kliplere çevirir.
    /// Tüm makineler aynı paketleri gördüğü için aynı klipleri üretir; PCM saklanmaz.
    /// </summary>
    public class ClipRecorder : MonoBehaviour
    {
        const float FinalizeSilence = 1.0f;
        const float MinSeconds = 0.4f;
        const float MaxSeconds = 6f;
        const int MaxPackets = 1500;

        const float ClassifyWindow = 3.0f;   // çağrı/cevap zaman penceresi
        const float ReplyMaxSeconds = 2.5f;
        const float ClassifyDelay = 3.2f;    // bitişten sonra bekleme (ileri bakış)
        const float SpanKeep = 15f;

        // Sunucu: konuşma zaman aralıkları (sıra alma analizi için)
        class Span
        {
            public ulong speaker;
            public int id;
            public float start, end;
        }

        class Open
        {
            public int id;
            public float start;
            public float last;
            public Span span; // yalnızca sunucuda
            public bool overflow;
            public readonly List<(byte codec, byte[] data)> packets = new List<(byte codec, byte[] data)>(64);
        }

        static ClipRecorder instance;
        static readonly Dictionary<ulong, Open> open = new Dictionary<ulong, Open>();
        static readonly List<Span> spans = new List<Span>(32);
        static readonly List<VoiceClip> pending = new List<VoiceClip>(8);
        static float[] decodeBuf = new float[4096];
        readonly List<ulong> keys = new List<ulong>(8);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create() => EnsureInstance();

        static void EnsureInstance()
        {
            if (instance != null) return;
            var go = new GameObject("ClipRecorder");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ClipRecorder>();
        }

        /// <summary>Açık (bitmemiş) kayıtları at (bağlantı koptu).</summary>
        public static void Reset()
        {
            open.Clear();
            spans.Clear();
            pending.Clear();
        }

        static bool IsServerNow()
        {
            var nm = NetworkManager.Singleton;
            return nm != null && nm.IsServer;
        }

        /// <summary>Bir ses paketi: sahip gönderirken kendi sesi için, alıcılar Receive'de çağırır.</summary>
        public static void OnPacket(ulong speaker, int utteranceId, byte codec, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            EnsureInstance();

            if (!ClipSharing.IsAllowed(speaker))
            {
                open.Remove(speaker); // gizlilik: bu konuşmacı taklit edilemez
                return;
            }

            open.TryGetValue(speaker, out var o);
            if (o != null && o.id != utteranceId)
            {
                FinalizeClip(speaker, o);
                open.Remove(speaker);
                o = null;
            }
            if (o == null)
            {
                o = new Open { id = utteranceId, start = Time.unscaledTime };
                if (IsServerNow())
                {
                    o.span = new Span { speaker = speaker, id = utteranceId, start = o.start, end = o.start };
                    spans.Add(o.span);
                }
                open[speaker] = o;
            }
            o.last = Time.unscaledTime;
            if (o.span != null) o.span.end = o.last;
            if (o.packets.Count >= MaxPackets) o.overflow = true;
            else o.packets.Add((codec, data));
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (pending.Count > 0 || spans.Count > 0) ServerUpdate(now);
            if (open.Count == 0) return;
            keys.Clear();
            foreach (var kv in open)
                if (now - kv.Value.last > FinalizeSilence) keys.Add(kv.Key);
            for (int i = 0; i < keys.Count; i++)
            {
                var o = open[keys[i]];
                open.Remove(keys[i]);
                FinalizeClip(keys[i], o);
            }
        }

        // Sunucu: bekleyen klipleri sınıfla, eski aralıkları at
        static void ServerUpdate(float now)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var c = pending[i];
                if (now < c.endTime + ClassifyDelay) continue;
                pending.RemoveAt(i);
                Classify(c);
            }
            for (int i = spans.Count - 1; i >= 0; i--)
                if (now - spans[i].end > SpanKeep) spans.RemoveAt(i);
        }

        static void Classify(VoiceClip c)
        {
            bool call = false, reply = false;
            for (int i = 0; i < spans.Count; i++)
            {
                var s = spans[i];
                if (s.speaker == c.speakerClientId) continue;
                if (s.start >= c.endTime && s.start - c.endTime <= ClassifyWindow) call = true;
                if (c.startTime >= s.end && c.startTime - s.end <= ClassifyWindow) reply = true;
            }
            if (c.duration > ReplyMaxSeconds) reply = false;
            c.isCall = call;
            c.isReply = reply;

            string kind = call && reply ? "Çağrı+Cevap" : call ? "Çağrı" : reply ? "Cevap" : "Diğer";
            Debug.Log($"[Klip] Sınıf: konuşmacı {c.speakerClientId}, no {c.id} -> {kind}");
            if (!c.IsConversational) return; // sunucuda yalnızca konuşma klipleri tutulur
            VoiceClipLibrary.Add(c, VoiceClipLibrary.MaxPerSpeaker);
        }

        static void FinalizeClip(ulong speaker, Open o)
        {
            if (o.overflow || o.packets.Count == 0) return;

            // Süreyi ölçmek için bir kez çöz
            float duration = 0f;
            int ok = 0;
            for (int i = 0; i < o.packets.Count; i++)
            {
                var pk = o.packets[i];
                if (!VoiceCodec.Decode(pk.codec, pk.data, pk.data.Length, ref decodeBuf, out int n, out int rate)) continue;
                duration += (float)n / rate;
                ok++;
            }
            if (ok == 0) return;
            if (duration < MinSeconds || duration > MaxSeconds) return;

            var clip = new VoiceClip
            {
                speakerClientId = speaker,
                id = o.id,
                packets = o.packets,
                duration = duration,
                startTime = o.start,
                endTime = o.last,
            };

            if (IsServerNow())
            {
                // Sunucu: 3 sn ileri bakış için bekle, sonra sınıfla
                pending.Add(clip);
                Debug.Log($"[Klip] Kaydedildi (beklemede): konuşmacı {speaker}, no {o.id}, süre {duration:0.0} sn");
            }
            else
            {
                VoiceClipLibrary.Add(clip, VoiceClipLibrary.MaxCache); // istemci: önbellek
                Debug.Log($"[Klip] Kaydedildi: konuşmacı {speaker}, no {o.id}, süre {duration:0.0} sn, toplam {VoiceClipLibrary.Count(speaker)}");
            }
        }
    }
}
