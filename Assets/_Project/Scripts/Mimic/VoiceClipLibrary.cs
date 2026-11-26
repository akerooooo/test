using System;
using System.Collections.Generic;

namespace MimicGame.Mimic
{
    public enum ClipTag { Short, General }

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
        public ClipTag tag;
    }

    /// <summary>Konuşmacı başına en fazla 30 klip tutan statik kütüphane.</summary>
    public static class VoiceClipLibrary
    {
        public const int MaxPerSpeaker = 30;
        public const float ShortLimit = 1.2f;

        static readonly Dictionary<ulong, List<VoiceClip>> clips = new Dictionary<ulong, List<VoiceClip>>();
        static readonly List<VoiceClip> empty = new List<VoiceClip>(0);

        public static event Action<VoiceClip> ClipAdded;

        public static void Add(VoiceClip clip)
        {
            if (clip == null || clip.packets == null || clip.packets.Count == 0) return;
            if (!clips.TryGetValue(clip.speakerClientId, out var list))
            {
                list = new List<VoiceClip>(MaxPerSpeaker + 1);
                clips[clip.speakerClientId] = list;
            }
            // Aynı kimlik varsa değiştir
            for (int i = 0; i < list.Count; i++)
                if (list[i].id == clip.id) { list.RemoveAt(i); break; }
            list.Add(clip);
            while (list.Count > MaxPerSpeaker) list.RemoveAt(0); // en eskiyi at
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

        public static void Clear() => clips.Clear();
    }
}
