using System.Collections.Generic;
using MimicGame.Voice;
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

        class Open
        {
            public int id;
            public float last;
            public bool overflow;
            public readonly List<(byte codec, byte[] data)> packets = new List<(byte codec, byte[] data)>(64);
        }

        static ClipRecorder instance;
        static readonly Dictionary<ulong, Open> open = new Dictionary<ulong, Open>();
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
        public static void Reset() => open.Clear();

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
                o = new Open { id = utteranceId };
                open[speaker] = o;
            }
            o.last = Time.unscaledTime;
            if (o.packets.Count >= MaxPackets) o.overflow = true;
            else o.packets.Add((codec, data));
        }

        void Update()
        {
            if (open.Count == 0) return;
            float now = Time.unscaledTime;
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

            VoiceClipLibrary.Add(new VoiceClip
            {
                speakerClientId = speaker,
                id = o.id,
                packets = o.packets,
                duration = duration,
                tag = duration < VoiceClipLibrary.ShortLimit ? ClipTag.Short : ClipTag.General,
            });
            Debug.Log($"[Klip] Kaydedildi: konuşmacı {speaker}, no {o.id}, süre {duration:0.0} sn, toplam {VoiceClipLibrary.Count(speaker)}");
        }
    }
}
