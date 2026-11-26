using System;
using System.Collections.Generic;
using UnityEngine;

namespace MimicGame.Voice
{
    /// <summary>
    /// Yalnızca sunucuda: konuşmacıların ses paketi zamanlarını izler. Konuşma bitince
    /// (>= 0.3 sn konuşmadan sonra 0.5 sn sessizlik) SpeechEnded olayını tetikler.
    /// </summary>
    public static class SpeechTracker
    {
        const float EndSilence = 0.5f;
        const float MinSpeech = 0.3f;

        class State
        {
            public float start, last;
            public Vector3 pos;
            public bool active;
        }

        static readonly Dictionary<ulong, State> states = new Dictionary<ulong, State>();
        static readonly List<ulong> ended = new List<ulong>();
        static int lastTickFrame = -1;

        /// <summary>(konuşan clientId, konuşmacı konumu)</summary>
        public static event Action<ulong, Vector3> SpeechEnded;

        public static void Mark(ulong clientId, Vector3 pos)
        {
            float now = Time.unscaledTime;
            if (!states.TryGetValue(clientId, out var s))
            {
                s = new State();
                states[clientId] = s;
            }
            if (!s.active) { s.active = true; s.start = now; }
            s.last = now;
            s.pos = pos;
        }

        /// <summary>Her karede (birden çok çağrı olsa da bir kez işler).</summary>
        public static void Tick()
        {
            if (lastTickFrame == Time.frameCount) return;
            lastTickFrame = Time.frameCount;
            if (states.Count == 0) return;

            float now = Time.unscaledTime;
            ended.Clear();
            foreach (var kv in states)
            {
                var s = kv.Value;
                if (s.active && now - s.last > EndSilence) ended.Add(kv.Key);
            }
            for (int i = 0; i < ended.Count; i++)
            {
                var s = states[ended[i]];
                s.active = false;
                if (s.last - s.start >= MinSpeech) SpeechEnded?.Invoke(ended[i], s.pos);
            }
        }

        public static void Clear() => states.Clear();
    }
}
