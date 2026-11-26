using System.Collections.Generic;
using System.Text;
using MimicGame.Environment;
using MimicGame.Level;
using MimicGame.Network;
using MimicGame.Player;
using MimicGame.Voice;
using Unity.Netcode;
using UnityEngine;

namespace MimicGame.Mimic
{
    /// <summary>Kaydedilen davranış parçasının bağlamı.</summary>
    public enum SnippetTag { Walk, LookAround, GroupIdle, Door, Stairs, Flee }

    /// <summary>10 Hz'lik tek örnek (başlangıç konumuna ve yaw'ına göre bağıl).</summary>
    public struct SnippetFrame
    {
        public const byte FCrouch = 1, FSprint = 2, FJump = 4, FLight = 8, FHolding = 16;

        public Vector3 rel;   // başlangıç konumuna göre, başlangıç yaw çerçevesinde
        public float yaw;     // başlangıç yaw'ına göre bağıl (derece)
        public float pitch;   // mutlak bakış eğimi (derece)
        public float speed;   // yatay hız (m/sn)
        public byte flags;
        public byte slot;

        public bool Crouch => (flags & FCrouch) != 0;
        public bool Sprint => (flags & FSprint) != 0;
        public bool Jump => (flags & FJump) != 0;
        public bool Light => (flags & FLight) != 0;
        public bool Holding => (flags & FHolding) != 0;
    }

    /// <summary>4-8 sn'lik bir davranış parçası: yol, bakış ve durum kanalları.</summary>
    public sealed class BehaviorSnippet
    {
        public ulong owner;
        public SnippetTag tag;
        public float duration;
        public SnippetFrame[] frames;
        public float netDist;     // başlangıçtan bitişe yatay mesafe
        public float netYawDeg;   // net yer değiştirmenin yönü (başlangıç yaw çerçevesinde)
        public float meanYaw;     // ortalama bağıl yaw
        public float meanPitch;
        public float vertical;    // dikey değişim aralığı
        public int jumps;
    }

    /// <summary>
    /// Yalnızca sunucuda: canlı oyuncuları 10 Hz örnekler, MovementProfile'ı günceller ve akışı
    /// 4-8 sn'lik, bağlam etiketli parçalara böler. Ölülerin verisi saklanır.
    /// </summary>
    public class BehaviorRecorder : MonoBehaviour
    {
        public const float Interval = 0.1f;
        const int MaxPerPlayer = 60;
        const float SprintThreshold = 4.5f;
        const float WalkMin = 0.5f;
        const float StillThreshold = 0.3f;
        const float SpeedAlpha = 0.06f;
        const float RatioAlpha = 0.01f;
        const float GroupRadius = 8f;
        const float GroupIdleRadius = 5f;

        struct Raw
        {
            public Vector3 pos;
            public float yaw, pitch, speed;
            public byte flags, slot;
            public bool near, place, door;
        }

        sealed class State
        {
            public MovementProfile prof = new MovementProfile();
            public readonly List<BehaviorSnippet> snippets = new List<BehaviorSnippet>(MaxPerPlayer + 1);
            public readonly List<Raw> buf = new List<Raw>(96);
            public float targetLen = 6f;
            public bool hasLast;
            public float lastYaw, lastPitch;
            public int lastJump;
            public float sprintRun;
            public float lastLookBehind = -10f;
            public readonly float[] yawHist = new float[6];
            public int yawCount;
            public PlayerInventory inv;
            public Flashlight fl;
            public PlayerNetwork pn;
        }

        static readonly Dictionary<ulong, State> states = new Dictionary<ulong, State>();
        static readonly StringBuilder dbg = new StringBuilder(64);

        float next;
        float lastSample;
        bool subscribed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            if (FindFirstObjectByType<BehaviorRecorder>() != null) return;
            var go = new GameObject("BehaviorRecorder");
            DontDestroyOnLoad(go);
            go.AddComponent<BehaviorRecorder>();
        }

        // ---------- Genel erişim ----------

        static State GetState(ulong clientId)
        {
            if (!states.TryGetValue(clientId, out var st))
            {
                st = new State();
                states[clientId] = st;
            }
            return st;
        }

        /// <summary>Oyuncunun profili; veri yoksa varsayılanlarla yeni profil oluşturur.</summary>
        public static MovementProfile GetProfile(ulong clientId) => GetState(clientId).prof;

        /// <summary>Oyuncunun toplam parça sayısı.</summary>
        public static int Count(ulong clientId)
            => states.TryGetValue(clientId, out var st) ? st.snippets.Count : 0;

        /// <summary>Oyuncunun belirli etiketli parça sayısı.</summary>
        public static int Count(ulong clientId, SnippetTag tag)
        {
            if (!states.TryGetValue(clientId, out var st)) return 0;
            int n = 0;
            for (int i = 0; i < st.snippets.Count; i++) if (st.snippets[i].tag == tag) n++;
            return n;
        }

        /// <summary>Oyuncunun verilen etiketten rastgele bir parçası (mümkünse 'except' dışında); yoksa null.</summary>
        public static BehaviorSnippet Pick(ulong clientId, SnippetTag tag, BehaviorSnippet except = null)
        {
            if (!states.TryGetValue(clientId, out var st)) return null;
            int n = 0;
            for (int i = 0; i < st.snippets.Count; i++)
                if (st.snippets[i].tag == tag && st.snippets[i] != except) n++;
            if (n == 0)
            {
                // yalnızca 'except' varsa onu ver
                for (int i = 0; i < st.snippets.Count; i++) if (st.snippets[i].tag == tag) return st.snippets[i];
                return null;
            }
            int k = Random.Range(0, n);
            for (int i = 0; i < st.snippets.Count; i++)
            {
                var s = st.snippets[i];
                if (s.tag != tag || s == except) continue;
                if (k-- == 0) return s;
            }
            return null;
        }

        /// <summary>F9 paneli: oyuncunun etiket başına parça sayıları.</summary>
        public static string DebugLine(ulong clientId)
        {
            dbg.Length = 0;
            dbg.Append("toplam ").Append(Count(clientId));
            var tags = (SnippetTag[])System.Enum.GetValues(typeof(SnippetTag));
            for (int i = 0; i < tags.Length; i++)
                dbg.Append(" | ").Append(TagName(tags[i])).Append(' ').Append(Count(clientId, tags[i]));
            return dbg.ToString();
        }

        public static string TagName(SnippetTag t)
        {
            switch (t)
            {
                case SnippetTag.Walk: return "Yürüyüş";
                case SnippetTag.LookAround: return "Bakınma";
                case SnippetTag.GroupIdle: return "Grupta bekleme";
                case SnippetTag.Door: return "Kapı";
                case SnippetTag.Stairs: return "Merdiven";
                default: return "Kaçış";
            }
        }

        // ---------- Olaylar ----------

        void OnSpeechEnded(ulong clientId, Vector3 pos) => GetState(clientId).prof.speech.Add();

        void OnDoorToggled(ulong clientId, bool nowOpen)
        {
            var p = GetState(clientId).prof;
            p.doorCloseRatio = Mathf.Lerp(p.doorCloseRatio, nowOpen ? 0f : 1f, 0.15f);
        }

        void Subscribe(bool on)
        {
            if (on == subscribed) return;
            subscribed = on;
            if (on)
            {
                SpeechTracker.SpeechEnded += OnSpeechEnded;
                Door.PlayerToggled += OnDoorToggled;
            }
            else
            {
                SpeechTracker.SpeechEnded -= OnSpeechEnded;
                Door.PlayerToggled -= OnDoorToggled;
            }
        }

        void OnDestroy() => Subscribe(false);

        // ---------- Örnekleme ----------

        void Update()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || !nm.IsListening)
            {
                Subscribe(false);
                if (states.Count > 0) states.Clear();
                return;
            }
            Subscribe(true);

            float now = Time.time;
            if (now < next) return;
            float dt = now - lastSample;
            lastSample = now;
            next = now + Interval;
            if (dt < 0.05f || dt > 0.5f) dt = Interval;

            var all = PlayerNetwork.All;
            for (int i = 0; i < all.Count; i++)
            {
                var pn = all[i];
                if (pn == null || !pn.IsSpawned) continue;
                var st = GetState(pn.OwnerClientId);
                if (st.pn != pn)
                {
                    st.pn = pn;
                    st.inv = pn.GetComponent<PlayerInventory>();
                    st.fl = pn.GetComponent<Flashlight>();
                }
                if (pn.IsDead)
                {
                    st.hasLast = false;
                    st.prof.hasLast = false;
                    st.buf.Clear();
                    continue; // ölüler örneklenmez (verisi saklanır)
                }
                Sample(pn, st, dt);
            }
        }

        void Sample(PlayerNetwork pn, State st, float dt)
        {
            var prof = st.prof;
            Vector3 pos = pn.transform.position;
            float yaw = pn.transform.eulerAngles.y;
            float pitch = pn.Pitch;
            int jc = pn.JumpCount;

            if (!st.hasLast)
            {
                st.hasLast = true;
                prof.lastPos = pos;
                prof.hasLast = true;
                st.lastYaw = yaw;
                st.lastPitch = pitch;
                st.lastJump = jc;
                st.yawCount = 0;
                return;
            }

            Vector3 d = pos - prof.lastPos;
            float dy = d.y;
            d.y = 0f;
            prof.lastPos = pos;
            float speed = d.magnitude / dt;
            if (speed > 20f || Mathf.Abs(dy) > 4f)
            {
                // ışınlanma: akış kopar
                st.buf.Clear();
                st.lastYaw = yaw; st.lastPitch = pitch; st.lastJump = jc; st.yawCount = 0;
                return;
            }
            prof.currentSpeed = speed;
            prof.sampleCount++;

            bool crouch = pn.IsCrouchingNet;
            bool light = st.fl != null && st.fl.IsOn;
            int slot = st.inv != null ? st.inv.SelectedSlot : 0;
            bool holding = st.inv != null && st.inv.Current != null;
            bool sprint = speed >= SprintThreshold;
            bool moving = speed > WalkMin;
            bool still = speed < StillThreshold;

            float yawRate = Mathf.DeltaAngle(st.lastYaw, yaw) / dt;
            float pitchRate = (pitch - st.lastPitch) / dt;
            st.lastYaw = yaw;
            st.lastPitch = pitch;

            bool jumped = jc != st.lastJump;
            if (jumped) { prof.jumps.Add(Mathf.Max(1, jc - st.lastJump)); st.lastJump = jc; }
            prof.jumps.Tick(dt);
            prof.lookBehind.Tick(dt);
            prof.speech.Tick(dt);

            // ---- Eski ortalamalar ----
            if (!crouch && speed > WalkMin && speed < SprintThreshold)
                prof.walkSpeed = Mathf.Lerp(prof.walkSpeed, speed, SpeedAlpha);
            else if (sprint)
                prof.sprintSpeed = Mathf.Lerp(prof.sprintSpeed, speed, SpeedAlpha);
            prof.sprintRatio = Mathf.Lerp(prof.sprintRatio, sprint ? 1f : 0f, RatioAlpha);
            prof.crouchRatio = Mathf.Lerp(prof.crouchRatio, crouch ? 1f : 0f, RatioAlpha);
            prof.flashlightRatio = Mathf.Lerp(prof.flashlightRatio, light ? 1f : 0f, RatioAlpha);
            prof.stillRatio = Mathf.Lerp(prof.stillRatio, still ? 1f : 0f, RatioAlpha);
            if (still) prof.stillTimer += dt;
            else
            {
                if (prof.stillTimer > 0.5f)
                    prof.avgStillDuration = Mathf.Clamp(Mathf.Lerp(prof.avgStillDuration, prof.stillTimer, 0.2f), 0.5f, 8f);
                prof.stillTimer = 0f;
            }

            // ---- Zengin profil ----
            float ay = Mathf.Abs(yawRate);
            if (ay > 8f && ay < 1500f)
            {
                prof.turnSpeedMean = Mathf.Lerp(prof.turnSpeedMean, ay, 0.03f);
                float diff = ay - prof.turnSpeedMean;
                prof.turnVar = Mathf.Lerp(prof.turnVar, diff * diff, 0.03f);
                prof.turnSpeedStd = Mathf.Sqrt(Mathf.Max(1f, prof.turnVar));
            }
            if (still && ay < 60f)
                prof.lookJitter = Mathf.Lerp(prof.lookJitter, ay + Mathf.Abs(pitchRate) * 0.5f, 0.02f);

            // geriye bakma: 0.5 sn içinde > 120 derece dönüş
            if (st.yawCount < st.yawHist.Length) st.yawHist[st.yawCount++] = yaw;
            else
            {
                for (int k = 0; k < st.yawHist.Length - 1; k++) st.yawHist[k] = st.yawHist[k + 1];
                st.yawHist[st.yawHist.Length - 1] = yaw;
                if (Mathf.Abs(Mathf.DeltaAngle(st.yawHist[0], yaw)) > 120f && Time.time - st.lastLookBehind > 1.5f)
                {
                    st.lastLookBehind = Time.time;
                    prof.lookBehind.Add();
                }
            }
            prof.lookBehindPerMin = prof.lookBehind.PerMinute(0.6f);
            prof.jumpPerMin = prof.jumps.PerMinute(0.5f);
            prof.speechPerMin = prof.speech.PerMinute(3f);

            if (moving) prof.crouchWalkRatio = Mathf.Lerp(prof.crouchWalkRatio, crouch ? 1f : 0f, 0.02f);

            // koşu atağı süresi
            if (sprint) st.sprintRun += dt;
            else if (st.sprintRun > 0f)
            {
                if (st.sprintRun >= 0.3f)
                    prof.sprintBurstMean = Mathf.Clamp(Mathf.Lerp(prof.sprintBurstMean, st.sprintRun, 0.2f), 0.5f, 15f);
                st.sprintRun = 0f;
            }

            prof.itemHoldRatio = Mathf.Lerp(prof.itemHoldRatio, holding ? 1f : 0f, RatioAlpha);

            // grup: en yakın canlı oyuncu
            PlayerNetwork nearest = null;
            float nd = float.MaxValue;
            var all = PlayerNetwork.All;
            for (int i = 0; i < all.Count; i++)
            {
                var o = all[i];
                if (o == null || o == pn || !o.IsSpawned || o.IsDead) continue;
                Vector3 od = o.transform.position - pos;
                if (Mathf.Abs(od.y) > 2.5f) continue;
                od.y = 0f;
                float m = od.magnitude;
                if (m < nd) { nd = m; nearest = o; }
            }
            if (nearest != null && nd < GroupRadius)
            {
                prof.groupSamples++;
                prof.groupDistMean = Mathf.Lerp(prof.groupDistMean, nd, 0.02f);
                Vector3 off = pos - nearest.transform.position; off.y = 0f;
                Vector3 fwd = nearest.transform.forward; fwd.y = 0f;
                if (off.sqrMagnitude > 0.25f && fwd.sqrMagnitude > 0.01f)
                {
                    float a = Vector3.SignedAngle(fwd, off, Vector3.up) * Mathf.Deg2Rad;
                    prof.groupVecX = Mathf.Lerp(prof.groupVecX, Mathf.Cos(a), 0.02f);
                    prof.groupVecY = Mathf.Lerp(prof.groupVecY, Mathf.Sin(a), 0.02f);
                    prof.groupAngleDeg = Mathf.Atan2(prof.groupVecY, prof.groupVecX) * Mathf.Rad2Deg;
                }
            }

            // ---- Ham örnek ----
            byte flags = 0;
            if (crouch) flags |= SnippetFrame.FCrouch;
            if (sprint) flags |= SnippetFrame.FSprint;
            if (jumped) flags |= SnippetFrame.FJump;
            if (light) flags |= SnippetFrame.FLight;
            if (holding) flags |= SnippetFrame.FHolding;

            bool place = !FacilityGenerator.IsInteriorPoint(pos) || FacilityGenerator.RoomAt(pos) != null;
            st.buf.Add(new Raw
            {
                pos = pos, yaw = yaw, pitch = pitch, speed = speed, flags = flags, slot = (byte)Mathf.Clamp(slot, 0, 255),
                near = nearest != null && nd < GroupIdleRadius, place = place, door = NearDoor(pos)
            });

            if (st.buf.Count * Interval >= st.targetLen) Finish(pn.OwnerClientId, st);
        }

        // Kapı açıklığından (iç kapı noktaları ya da tesis giriş/çıkış kapısı) geçiyor mu
        static bool NearDoor(Vector3 pos)
        {
            var pts = FacilityGenerator.DoorPoints;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 dv = pts[i] - pos;
                if (Mathf.Abs(dv.y) > 2f) continue;
                dv.y = 0f;
                if (dv.sqrMagnitude < 1f) return true;
            }
            var ext = FacilityDoor.Exterior;
            if (ext != null && (ext.transform.position - pos).sqrMagnitude < 2.25f) return true;
            var inte = FacilityDoor.Interior;
            if (inte != null && (inte.transform.position - pos).sqrMagnitude < 2.25f) return true;
            return false;
        }

        // ---------- Parça üretimi ----------

        void Finish(ulong clientId, State st)
        {
            var buf = st.buf;
            int n = buf.Count;
            st.targetLen = Random.Range(4f, 8f);
            if (n < 20)
            {
                buf.Clear();
                return;
            }
            float dur = n * Interval;

            float dist = 0f, yMin = float.MaxValue, yMax = float.MinValue;
            int still = 0, sprint = 0, near = 0, place = 0, jumps = 0;
            bool door = false;
            float yawSum = 0f, pitchSum = 0f, pitchMin = float.MaxValue, pitchMax = float.MinValue;
            float startYaw = buf[0].yaw;
            Vector3 startPos = buf[0].pos;
            for (int i = 0; i < n; i++)
            {
                var r = buf[i];
                dist += r.speed * Interval;
                if (r.pos.y < yMin) yMin = r.pos.y;
                if (r.pos.y > yMax) yMax = r.pos.y;
                if (r.speed < StillThreshold) still++;
                if (r.speed >= SprintThreshold) sprint++;
                if (r.near) near++;
                if (r.place) place++;
                if (r.door) door = true;
                if ((r.flags & SnippetFrame.FJump) != 0) jumps++;
                yawSum += Mathf.Abs(Mathf.DeltaAngle(i > 0 ? buf[i - 1].yaw : r.yaw, r.yaw));
                pitchSum += r.pitch;
                if (r.pitch < pitchMin) pitchMin = r.pitch;
                if (r.pitch > pitchMax) pitchMax = r.pitch;
            }
            float avgSpeed = dist / dur;
            float vertical = yMax - yMin;
            float stillFrac = still / (float)n;
            float sprintFrac = sprint / (float)n;
            float nearFrac = near / (float)n;
            float placeFrac = place / (float)n;
            bool viewChange = yawSum > 25f || (pitchMax - pitchMin) > 8f;

            SnippetTag tag;
            if (avgSpeed > 0.5f && vertical > 1.5f) tag = SnippetTag.Stairs;
            else if (sprintFrac > 0.6f) tag = SnippetTag.Flee;
            else if (door && avgSpeed > 0.5f) tag = SnippetTag.Door;
            else if (stillFrac > 0.8f && nearFrac > 0.7f) tag = SnippetTag.GroupIdle;
            else if (stillFrac > 0.7f && placeFrac > 0.7f && viewChange) tag = SnippetTag.LookAround;
            else if (avgSpeed > 1f) tag = SnippetTag.Walk;
            else
            {
                buf.Clear();
                return; // ilginç değil
            }

            var snip = new BehaviorSnippet
            {
                owner = clientId,
                tag = tag,
                duration = dur,
                frames = new SnippetFrame[n],
                vertical = vertical,
                jumps = jumps,
                meanPitch = pitchSum / n
            };
            var inv = Quaternion.Euler(0f, -startYaw, 0f);
            float yawRelSum = 0f;
            for (int i = 0; i < n; i++)
            {
                var r = buf[i];
                float yr = Mathf.DeltaAngle(startYaw, r.yaw);
                yawRelSum += yr;
                snip.frames[i] = new SnippetFrame
                {
                    rel = inv * (r.pos - startPos),
                    yaw = yr,
                    pitch = r.pitch,
                    speed = r.speed,
                    flags = r.flags,
                    slot = r.slot
                };
            }
            snip.meanYaw = yawRelSum / n;
            Vector3 end = snip.frames[n - 1].rel;
            snip.netDist = new Vector2(end.x, end.z).magnitude;
            snip.netYawDeg = Mathf.Atan2(end.x, end.z) * Mathf.Rad2Deg;
            buf.Clear();

            Add(clientId, st, snip);
        }

        void Add(ulong clientId, State st, BehaviorSnippet snip)
        {
            st.snippets.Add(snip);
            if (st.snippets.Count > MaxPerPlayer)
            {
                // en kalabalık etiketin en eski parçasını at (adil silme)
                int[] counts = new int[6];
                for (int i = 0; i < st.snippets.Count; i++) counts[(int)st.snippets[i].tag]++;
                int worst = 0;
                for (int t = 1; t < counts.Length; t++) if (counts[t] > counts[worst]) worst = t;
                for (int i = 0; i < st.snippets.Count; i++)
                    if ((int)st.snippets[i].tag == worst) { st.snippets.RemoveAt(i); break; }
            }
            Debug.Log("[Davranış] Parça kaydedildi: oyuncu " + clientId + ", tür " + TagName(snip.tag) + ", süre "
                + snip.duration.ToString("0.0") + " sn, toplam " + st.snippets.Count);
        }
    }
}
