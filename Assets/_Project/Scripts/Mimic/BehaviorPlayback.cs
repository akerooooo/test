using UnityEngine;
using UnityEngine.AI;

namespace MimicGame.Mimic
{
    /// <summary>
    /// Yalnızca sunucuda (MimicAI içinde): kaydedilmiş bir davranış parçasını oynatır.
    /// Bakış, eğilme, fener, zıplama ve gövde yönü kanallarını sürer; yol NavMesh'te geçerliyse ajanı
    /// kaydedilen hızlarda yürütür. Unity nesnesi tutmaz (alan başlatıcısında güvenli).
    /// </summary>
    public sealed class BehaviorPlayback
    {
        const float WaypointStep = 0.5f;   // sn
        const float SampleRadius = 0.6f;   // m
        const float MaxLag = 5f;           // ajan bu kadar geride kalırsa oynatma iptal

        public BehaviorSnippet Snippet { get; private set; }
        public bool IsPlaying { get; private set; }
        /// <summary>Ajanı bu oynatma sürüyor mu (yol geçerli).</summary>
        public bool Moving { get; private set; }
        public float Elapsed { get; private set; }

        // Çıktı kanalları (MimicAI uygular)
        public bool Crouch, Flashlight, Holding, Sprint;
        public float Pitch;
        public float Yaw;          // dünya yaw'ı (derece)
        public int JumpPulse;      // her zıplama karesinde artar
        /// <summary>Doğruysa ajan komutları askıda (örn. kapı açılırken).</summary>
        public bool Paused;

        Vector3[] wps;
        Vector3 startPoint;
        float[] wpTime;
        int wpCount;
        float startTime;
        float baseYaw, pitchOffset;
        int lastFrame;
        float nextDest;
        Transform self;
        NavMeshAgent agent;

        /// <summary>F9 paneli için kısa açıklama.</summary>
        public string Label => IsPlaying && Snippet != null
            ? BehaviorRecorder.TagName(Snippet.tag) + " (oyuncu " + Snippet.owner + ", " + Elapsed.ToString("0.0") + "/" + Snippet.duration.ToString("0.0") + " sn" + (Moving ? ", yürüyor" : ", duruyor") + ")"
            : "yok";

        /// <summary>
        /// Parçayı başlatır. baseYawDeg: parçanın başlangıç yaw çerçevesinin dünya karşılığı.
        /// allowMove: yol oynatılabilir mi; requireMove: yol geçersizse oynatma hiç başlamasın.
        /// </summary>
        public bool Begin(BehaviorSnippet s, Transform self, NavMeshAgent agent, float now,
            float baseYawDeg, float pitchOffsetDeg, bool allowMove, bool requireMove)
        {
            Stop();
            if (s == null || s.frames == null || s.frames.Length < 2 || self == null) return false;
            this.self = self;
            this.agent = agent;
            baseYaw = baseYawDeg;
            pitchOffset = pitchOffsetDeg;

            bool wantsMove = s.netDist > 1f;
            bool moving = false;
            if (wantsMove && allowMove && agent != null && agent.enabled && agent.isOnNavMesh)
                moving = BuildPath(s);
            if (wantsMove && requireMove && !moving) return false;

            Snippet = s;
            Moving = moving;
            IsPlaying = true;
            Paused = false;
            startTime = now;
            Elapsed = 0f;
            lastFrame = -1;
            nextDest = 0f;
            Sprint = false;
            Yaw = baseYaw + s.frames[0].yaw;
            Pitch = Mathf.Clamp(s.frames[0].pitch + pitchOffset, -45f, 45f);
            Crouch = s.frames[0].Crouch;
            Flashlight = s.frames[0].Light;
            Holding = s.frames[0].Holding;
            return true;
        }

        // Göreli yolu dünya uzayına çevirir; her nokta NavMesh'e oturmalı ve ardışık noktalar arası engelsiz olmalı
        bool BuildPath(BehaviorSnippet s)
        {
            int n = s.frames.Length;
            int step = Mathf.Max(1, Mathf.RoundToInt(WaypointStep / BehaviorRecorder.Interval));
            int cap = n / step + 2;
            if (wps == null || wps.Length < cap) { wps = new Vector3[cap]; wpTime = new float[cap]; }
            wpCount = 0;

            Vector3 origin = self.position;
            var rot = Quaternion.Euler(0f, baseYaw, 0f);
            Vector3 prev = origin;
            if (NavMesh.SamplePosition(origin, out var h0, SampleRadius, NavMesh.AllAreas)) prev = h0.position;
            startPoint = prev;

            for (int i = step; i < n + step - 1; i += step)
            {
                int fi = Mathf.Min(i, n - 1);
                Vector3 world = origin + rot * s.frames[fi].rel;
                if (!NavMesh.SamplePosition(world, out var hit, SampleRadius, NavMesh.AllAreas)) return false;
                if (NavMesh.Raycast(prev, hit.position, out _, NavMesh.AllAreas)) return false; // true = engel var
                float flat = new Vector2(hit.position.x - prev.x, hit.position.z - prev.z).magnitude;
                if (flat < 0.3f && fi < n - 1) continue; // gereksiz sık nokta
                wps[wpCount] = hit.position;
                wpTime[wpCount] = fi * BehaviorRecorder.Interval;
                wpCount++;
                prev = hit.position;
                if (fi >= n - 1) break;
            }
            return wpCount > 0;
        }

        /// <summary>Oynatmayı bitirir ve ajan yolunu temizler.</summary>
        public void Stop()
        {
            bool wasMoving = IsPlaying && Moving;
            IsPlaying = false;
            Moving = false;
            Paused = false;
            Snippet = null;
            if (wasMoving && agent != null && agent.enabled && agent.isOnNavMesh) agent.ResetPath();
        }

        /// <summary>Bir adım ilerler; hâlâ oynatılıyorsa true.</summary>
        public bool Tick(float now)
        {
            if (!IsPlaying || Snippet == null) return false;
            Elapsed = now - startTime;
            var fr = Snippet.frames;
            if (Elapsed >= Snippet.duration) { Stop(); return false; }

            float ft = Elapsed / BehaviorRecorder.Interval;
            int fi = Mathf.Clamp(Mathf.FloorToInt(ft), 0, fr.Length - 1);
            int fn = Mathf.Min(fi + 1, fr.Length - 1);
            float frac = Mathf.Clamp01(ft - fi);

            // zıplama kareleri (atlanan kareler dahil)
            for (int k = lastFrame + 1; k <= fi; k++)
                if (k >= 0 && fr[k].Jump) JumpPulse++;
            lastFrame = fi;

            var f = fr[fi];
            Yaw = baseYaw + Mathf.LerpAngle(f.yaw, fr[fn].yaw, frac);
            Pitch = Mathf.Clamp(Mathf.Lerp(f.pitch, fr[fn].pitch, frac) + pitchOffset, -45f, 45f);
            Crouch = f.Crouch;
            Flashlight = f.Light;
            Holding = f.Holding;
            Sprint = f.Sprint;

            if (Moving && !TickMovement(now, f))
            {
                Stop();
                return false;
            }
            return true;
        }

        bool TickMovement(float now, SnippetFrame f)
        {
            if (agent == null || !agent.enabled || !agent.isOnNavMesh) return false;
            if (Paused) return true;

            // Şu anki zamanın beklenen konumu (nokta dizisi üzerinde doğrusal)
            Vector3 expected = wps[wpCount - 1];
            int target = wpCount - 1;
            for (int i = 0; i < wpCount; i++)
            {
                if (wpTime[i] >= Elapsed)
                {
                    Vector3 a = i > 0 ? wps[i - 1] : startPoint;
                    float t0 = i > 0 ? wpTime[i - 1] : 0f;
                    float t = Mathf.InverseLerp(t0, wpTime[i], Elapsed);
                    expected = Vector3.Lerp(a, wps[i], t);
                    target = i;
                    break;
                }
            }
            float lag = new Vector2(self.position.x - expected.x, self.position.z - expected.z).magnitude;
            if (lag > MaxLag) return false;

            if (now >= nextDest)
            {
                nextDest = now + 0.2f;
                // biraz ilerideki noktaya yönel
                int ahead = Mathf.Min(wpCount - 1, target + (lag > 1f ? 1 : 0));
                agent.stoppingDistance = 0.1f;
                agent.isStopped = false;
                agent.SetDestination(wps[ahead]);
            }
            float rec = Mathf.Clamp(f.speed, 0.6f, 6.5f);
            float catchUp = 1f + Mathf.Clamp(lag - 0.8f, 0f, 2f) * 0.25f;
            agent.speed = Mathf.Min(6.8f, rec * catchUp);
            return true;
        }
    }
}
