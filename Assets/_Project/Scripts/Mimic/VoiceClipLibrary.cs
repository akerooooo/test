using System;
using System.Collections.Generic;

namespace MimicGame.Mimic
{
    /// <summary>
    /// Kaydedilmiş bir konuşma: canlı sesle birebir aynı ses paketleri. PCM tutulmaz;
    /// oynatırken VoiceCodec ile çözülür.
    /// </summary>
    public class VoiceClip
    {
        public ulong speakerClientId;
        public int id;
        public List<(byte codec, byte[] data)> packets;
        public float duration;
        public float startTime, endTime; // sunucunun aldığı zaman (unscaled)
        public bool isCall;              // başka bir oyuncu 3 sn içinde yanıtladı
        public bool isReply;             // başkasının konuşmasından sonra 3 sn içinde, <= 2.5 sn
        public bool IsConversational => isCall || isReply;
    }

    /// <summary>Konuşmacı başına klip tutan statik kütüphane (sunucuda 30 konuşma klibi, istemcide 40 önbellek).</summary>
    public static class VoiceClipLibrary
    {
        public const int MaxPerSpeaker = 30;
        public const int MaxCache = 40; // istemci önbelleği

        static readonly Dictionary<ulong, List<VoiceClip>> clips = new Dictionary<ulong, List<VoiceClip>>();
        static readonly List<VoiceClip> empty = new List<VoiceClip>(0);

        public static event Action<VoiceClip> ClipAdded;

        public static void Add(VoiceClip clip, int cap)
        {
            if (clip == null || clip.packets == null || clip.packets.Count == 0) return;
            if (!clips.TryGetValue(clip.speakerClientId, out var list))
            {
                list = new List<VoiceClip>(cap + 1);
                clips[clip.speakerClientId] = list;
            }
            // Aynı kimlik varsa değiştir
            for (int i = 0; i < list.Count; i++)
                if (list[i].id == clip.id) { list.RemoveAt(i); break; }
            list.Add(clip);
            while (list.Count > cap) list.RemoveAt(0); // en eskiyi at
            ClipAdded?.Invoke(clip);
        }

        public static bool TryGet(ulong speaker, int id, out VoiceClip clip)
        {
            if (clips.TryGetValue(speaker, out var list))
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i].id == id) { clip = list[i]; return true; }
            }
            clip = null;
            return false;
        }

        public static List<VoiceClip> GetClips(ulong speaker)
            => clips.TryGetValue(speaker, out var list) ? list : empty;

        public static int Count(ulong speaker)
            => clips.TryGetValue(speaker, out var list) ? list.Count : 0;

        public static int CountCalls(ulong speaker)
        {
            if (!clips.TryGetValue(speaker, out var list)) return 0;
            int n = 0;
            for (int i = 0; i < list.Count; i++) if (list[i].isCall) n++;
            return n;
        }

        public static int CountReplies(ulong speaker)
        {
            if (!clips.TryGetValue(speaker, out var list)) return 0;
            int n = 0;
            for (int i = 0; i < list.Count; i++) if (list[i].isReply) n++;
            return n;
        }

        public static void Clear() => clips.Clear();
    }
}
