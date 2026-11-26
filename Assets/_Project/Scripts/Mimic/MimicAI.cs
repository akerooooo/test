using System.Collections.Generic;
using MimicGame.Network;
using MimicGame.Voice;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace MimicGame.Mimic
{
    /// <summary>
    /// Taklitçi: seçtiği oyuncunun görünümünü, hareket alışkanlıklarını ve sesini kopyalar.
    /// Sunucu yetkili; istemciler yalnızca görünümü uygular ve konuşma RPC'sini çalar.
    /// (Saldırı henüz yok.)
    /// </summary>
    public class MimicAI : NetworkBehaviour
    {
        public static readonly List<MimicAI> All = new List<MimicAI>();

        [Header("Referanslar")]
        public Transform body;
        public Renderer bodyRenderer;
        public Transform cameraPivot;
        public Light spot;
        public VoicePlayer voicePlayer;

        [Header("Bina (tercih edilen bölge)")]
        public Vector3 buildingCenter = new Vector3(0f, 0f, 20f);
        public Vector2 buildingHalfSize = new Vector2(10f, 10f);

        // Oyuncuyla aynı boylar
        const float StandHeight = 1.8f;
        const float CrouchHeight = 1.0f;
        const float CrouchTransition = 6f;
        const float EyeOffsetFromTop = 0.15f;
        const float SprintThreshold = 4.5f;

        enum Mode { Wander, Follow, Lure }

        readonly NetworkVariable<ulong> targetClientId = new NetworkVariable<ulong>(ulong.MaxValue,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        readonly NetworkVariable<int> colorIndex = new NetworkVariable<int>(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        readonly NetworkVariable<bool> crouching = new NetworkVariable<bool>(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        readonly NetworkVariable<bool> flashlightOn = new NetworkVariable<bool>(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        readonly NetworkVariable<float> pitch = new NetworkVariable<float>(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public ulong TargetClientId => targetClientId.Value;

        NavMeshAgent agent;
        float shownHeight = StandHeight;
        float shownPitch;

        // ---- Sunucu durumu ----
        Mode mode = Mode.Wander;
        bool agentReady;
        float nextDecision, nextRetarget;
        float waitUntil;
        bool waiting;
        float destTimeout;
        bool hasDest;

        // Takip
        float nextFollowPath;

        // Konuşma
        float speakCooldownUntil;
        float pendingSpeakAt = -1f;
        float lureCooldownUntil;
        readonly int[] recentClips = { -1, -1, -1 };

        // Cezbetme
        Vector3 lureSpot;
        ulong lureVictim;
        float lureDeadline;
        bool lureSpoken;

        // Bakış
        bool hasLook;
        Vector3 lookPoint;
        readonly List<PlayerNetwork> tmpPlayers = new List<PlayerNetwork>(8);

        void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            if (agent != null)
            {
                agent.enabled = false; // yalnızca sunucuda açılır
            }
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            colorIndex.OnValueChanged += OnColorChanged;
            PlayerAppearance.ApplyColor(bodyRenderer, colorIndex.Value);
            shownHeight = crouching.Value ? CrouchHeight : StandHeight;
            shownPitch = pitch.Value;
            if (spot != null) spot.enabled = flashlightOn.Value;

            if (!IsServer) return;
            SpeechTracker.SpeechEnded += OnSpeechEnded;
            PickTarget();
            nextRetarget = Time.time + 30f;
            speakCooldownUntil = Time.time + Random.Range(4f, 8f);
            lureCooldownUntil = Time.time + Random.Range(10f, 20f);
            TryEnableAgent();
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            colorIndex.OnValueChanged -= OnColorChanged;
            SpeechTracker.SpeechEnded -= OnSpeechEnded;
        }

        void OnColorChanged(int oldValue, int newValue) => PlayerAppearance.ApplyColor(bodyRenderer, newValue);

        // ---------- Ortak görünüm ----------

        void Update()
        {
            if (!IsSpawned) return;
            ApplyVisuals();
            if (IsServer) ServerTick();
        }

        void ApplyVisuals()
        {
            float h = crouching.Value ? CrouchHeight : StandHeight;
            shownHeight = Mathf.MoveTowards(shownHeight, h, CrouchTransition * Time.deltaTime);
            shownPitch = Mathf.Lerp(shownPitch, pitch.Value, 1f - Mathf.Exp(-20f * Time.deltaTime));
            PlayerAppearance.ApplyBody(body, shownHeight);
            if (cameraPivot != null)
            {
                cameraPivot.localPosition = new Vector3(0f, shownHeight - EyeOffsetFromTop, 0f);
                cameraPivot.localRotation = Quaternion.Euler(shownPitch, 0f, 0f);
            }
            if (spot != null) spot.enabled = flashlightOn.Value;
        }

        // ---------- Konuşma ----------

        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Reliable, InvokePermission = RpcInvokePermission.Server)]
        void SpeakRpc(ulong speakerClientId, int clipId)
        {
            if (voicePlayer == null) return;
            if (!VoiceClipLibrary.TryGet(speakerClientId, clipId, out var clip)) return;
            voicePlayer.PlayClip(clip.samples, clip.sampleRate);
        }

        // ---------- Sunucu ----------

        void TryEnableAgent()
        {
            if (agentReady || agent == null) return;
            if (NavMesh.SamplePosition(transform.position, out var hit, 6f, NavMesh.AllAreas))
            {
                transform.position = hit.position;
                agent.enabled = true;
                agent.updateRotation = false; // dönüşü biz yönetiriz
                agent.Warp(hit.position);
                agentReady = agent.isOnNavMesh;
            }
        }

        void PickTarget()
        {
            var nm = NetworkManager;
            if (nm == null) return;
            var withClips = new List<ulong>();
            var any = new List<ulong>();
            var list = nm.ConnectedClientsList;
            for (int i = 0; i < list.Count; i++)
            {
                ulong id = list[i].ClientId;
                any.Add(id);
                if (VoiceClipLibrary.Count(id) > 0) withClips.Add(id);
            }
            var pool = withClips.Count > 0 ? withClips : any;
            if (pool.Count == 0) return;
            ulong chosen = pool[Random.Range(0, pool.Count)];
            targetClientId.Value = chosen;
            var po = nm.SpawnManager.GetPlayerNetworkObject(chosen);
            var pn = po != null ? po.GetComponent<PlayerNetwork>() : null;
            colorIndex.Value = pn != null ? pn.ColorIndex : (int)(chosen % (ulong)PlayerAppearance.Palette.Length);
        }

        void CheckRetarget()
        {
            var nm = NetworkManager;
            if (nm == null) return;
            ulong cur = targetClientId.Value;
            bool connected = nm.ConnectedClients.ContainsKey(cur);
            bool hasClips = VoiceClipLibrary.Count(cur) > 0;
            if (!connected) { PickTarget(); return; }
            if (hasClips) return;
            // Mevcut hedefin klibi yok; klibi olan biri varsa ona geç
            var list = nm.ConnectedClientsList;
            for (int i = 0; i < list.Count; i++)
                if (VoiceClipLibrary.Count(list[i].ClientId) > 0) { PickTarget(); return; }
        }

        void ServerTick()
        {
            float now = Time.time;
            if (now >= nextRetarget)
            {
                nextRetarget = now + 30f;
                CheckRetarget();
            }

            if (!agentReady)
            {
                TryEnableAgent();
                if (!agentReady) return;
            }

            if (pendingSpeakAt > 0f && now >= pendingSpeakAt)
            {
                pendingSpeakAt = -1f;
                if (now >= speakCooldownUntil && TrySpeak(true)) speakCooldownUntil = now + Random.Range(6f, 14f);
            }

            if (now >= nextDecision)
            {
                nextDecision = now + 0.25f;
                Decide(now);
            }

            UpdateLook();
        }

        void CollectPlayers()
        {
            tmpPlayers.Clear();
            var all = PlayerNetwork.All;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].IsSpawned) tmpPlayers.Add(all[i]);
        }

        static Vector3 Head(PlayerNetwork p)
            => p.cameraPivot != null ? p.cameraPivot.position : p.transform.position + Vector3.up * 1.6f;

        static float FlatDist(Vector3 a, Vector3 b)
        {
            a.y = 0f; b.y = 0f;
            return Vector3.Distance(a, b);
        }

        bool InsideBuilding(Vector3 p)
            => Mathf.Abs(p.x - buildingCenter.x) < buildingHalfSize.x - 0.5f &&
               Mathf.Abs(p.z - buildingCenter.z) < buildingHalfSize.y - 0.5f;

        void Decide(float now)
        {
            var prof = MovementProfiler.Get(targetClientId.Value);
            CollectPlayers();

            // En yakın oyuncu
            PlayerNetwork nearest = null;
            float nearestD = float.MaxValue;
            for (int i = 0; i < tmpPlayers.Count; i++)
            {
                float d = FlatDist(transform.position, tmpPlayers[i].transform.position);
                if (d < nearestD) { nearestD = d; nearest = tmpPlayers[i]; }
            }

            // Cezbetme sürüyorsa devam et
            if (mode == Mode.Lure)
            {
                UpdateLure(now, prof);
                return;
            }

            // Yeni cezbetme: yalnız kalmış, 25 m içinde bir oyuncu
            if (now >= lureCooldownUntil && StartLure(now))
            {
                UpdateLure(now, prof);
                return;
            }

            if (nearest != null && nearestD <= 15f)
            {
                if (mode != Mode.Follow) { mode = Mode.Follow; waiting = false; hasDest = false; nextFollowPath = 0f; }
                UpdateFollow(now, prof, nearest);
            }
            else
            {
                if (mode != Mode.Wander) { mode = Mode.Wander; waiting = false; hasDest = false; }
                hasLook = false;
                UpdateWander(now, prof);
            }
        }

        // ---- Gezinme ----

        void UpdateWander(float now, MovementProfile prof)
        {
            if (waiting)
            {
                if (now < waitUntil) return;
                waiting = false;
                hasDest = false;
            }
            if (!hasDest)
            {
                PickWanderDestination(prof);
                return;
            }

            bool arrived = !agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.15f;
            if (arrived || now > destTimeout)
            {
                // Varınca hedefin avgStillDuration süresi kadar dur
                agent.ResetPath();
                hasDest = false;
                waiting = true;
                waitUntil = now + prof.avgStillDuration * Random.Range(0.6f, 1.4f);
            }
        }

        void PickWanderDestination(MovementProfile prof)
        {
            Vector3 chosen = transform.position;
            bool found = false;
            for (int i = 0; i < 8 && !found; i++)
            {
                var c = transform.position + Random.insideUnitSphere * 15f;
                c.y = transform.position.y;
                if (!NavMesh.SamplePosition(c, out var hit, 3f, NavMesh.AllAreas)) continue;
                if (InsideBuilding(hit.position) || i == 7) { chosen = hit.position; found = true; }
            }
            if (!found) { waitUntil = Time.time + 1f; waiting = true; return; }

            bool crouch = Random.value < prof.crouchRatio;
            crouching.Value = crouch;
            flashlightOn.Value = InsideBuilding(chosen) && Random.value < prof.flashlightRatio;
            agent.speed = crouch ? 1.8f : prof.walkSpeed;
            agent.stoppingDistance = 0.3f;
            agent.SetDestination(chosen);
            destTimeout = Time.time + 25f;
            hasDest = true;
        }

        // ---- Gruba eşlik ----

        void UpdateFollow(float now, MovementProfile prof, PlayerNetwork nearest)
        {
            if (crouching.Value) crouching.Value = false;
            flashlightOn.Value = InsideBuilding(transform.position) && flashlightOn.Value;

            // Küme merkezi: en yakın oyuncunun 6 m içindekiler
            Vector3 centroid = Vector3.zero;
            int n = 0;
            for (int i = 0; i < tmpPlayers.Count; i++)
            {
                if (FlatDist(tmpPlayers[i].transform.position, nearest.transform.position) <= 6f)
                {
                    centroid += tmpPlayers[i].transform.position;
                    n++;
                }
            }
            centroid /= Mathf.Max(1, n);

            hasLook = true;
            lookPoint = Head(nearest);

            float nearestSpeed = MovementProfiler.Get(nearest.OwnerClientId).currentSpeed;
            agent.speed = nearestSpeed > SprintThreshold ? prof.sprintSpeed : prof.walkSpeed;
            agent.stoppingDistance = 0.5f;

            float dist = FlatDist(transform.position, centroid);
            if (dist >= 5f && dist <= 9f)
            {
                agent.ResetPath();
                return;
            }
            if (now < nextFollowPath) return;
            nextFollowPath = now + 0.6f;

            Vector3 away = transform.position - centroid;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = transform.forward;
            Vector3 want = centroid + away.normalized * 7f;
            if (NavMesh.SamplePosition(want, out var hit, 4f, NavMesh.AllAreas)) agent.SetDestination(hit.position);
        }

        // ---- Cezbetme ----

        bool StartLure(float now)
        {
            ulong tid = targetClientId.Value;
            if (VoiceClipLibrary.Count(tid) == 0) return false;

            for (int i = 0; i < tmpPlayers.Count; i++)
            {
                var victim = tmpPlayers[i];
                if (FlatDist(transform.position, victim.transform.position) > 25f) continue;

                bool isolated = true;
                for (int j = 0; j < tmpPlayers.Count && isolated; j++)
                {
                    if (j == i) continue;
                    if (FlatDist(tmpPlayers[j].transform.position, victim.transform.position) <= 12f) isolated = false;
                }
                if (!isolated) continue;

                if (!FindLureSpot(victim, out var spotPos)) continue;
                mode = Mode.Lure;
                lureSpot = spotPos;
                lureVictim = victim.OwnerClientId;
                lureDeadline = now + 20f;
                lureSpoken = false;
                waiting = false;
                hasDest = false;
                crouching.Value = false;
                flashlightOn.Value = false;
                agent.speed = MovementProfiler.Get(tid).walkSpeed;
                agent.stoppingDistance = 0.5f;
                agent.SetDestination(lureSpot);
                return true;
            }
            // Bulunamadıysa kısa süre sonra tekrar dene
            lureCooldownUntil = now + 5f;
            return false;
        }

        bool FindLureSpot(PlayerNetwork victim, out Vector3 result)
        {
            result = default;
            Vector3 head = Head(victim);
            var path = new NavMeshPath();
            for (int i = 0; i < 14; i++)
            {
                float ang = Random.value * Mathf.PI * 2f;
                float r = Random.Range(8f, 12f);
                var c = victim.transform.position + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
                if (!NavMesh.SamplePosition(c, out var hit, 3f, NavMesh.AllAreas)) continue;
                // Oyuncunun görüş hattında olmayan nokta
                if (!Physics.Linecast(head, hit.position + Vector3.up * 1.6f, ~0, QueryTriggerInteraction.Ignore)) continue;
                if (!NavMesh.CalculatePath(transform.position, hit.position, NavMesh.AllAreas, path)) continue;
                if (path.status != NavMeshPathStatus.PathComplete) continue;
                result = hit.position;
                return true;
            }
            return false;
        }

        void UpdateLure(float now, MovementProfile prof)
        {
            PlayerNetwork victim = FindPlayer(lureVictim);
            bool arrived = agent.hasPath && !agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.3f;
            if (victim == null || now > lureDeadline) { EndLure(now); return; }

            hasLook = true;
            lookPoint = Head(victim);

            if (arrived)
            {
                if (!lureSpoken)
                {
                    lureSpoken = true;
                    TrySpeak(true); // "buraya gel" gibi kısa çağrı
                    speakCooldownUntil = now + Random.Range(6f, 14f);
                    lureDeadline = now + 2f;
                }
            }
        }

        void EndLure(float now)
        {
            mode = Mode.Wander;
            waiting = false;
            hasDest = false;
            hasLook = false;
            lureCooldownUntil = now + Random.Range(20f, 40f);
            agent.ResetPath();
        }

        PlayerNetwork FindPlayer(ulong clientId)
        {
            for (int i = 0; i < tmpPlayers.Count; i++)
                if (tmpPlayers[i].OwnerClientId == clientId) return tmpPlayers[i];
            return null;
        }

        // ---- Konuşma tetikleri ----

        void OnSpeechEnded(ulong clientId, Vector3 speakerPos)
        {
            if (!IsServer || !IsSpawned) return;
            if (FlatDist(transform.position, speakerPos) > 14f) return;
            if (pendingSpeakAt > 0f || Time.time < speakCooldownUntil) return;
            pendingSpeakAt = Time.time + Random.Range(0.6f, 1.8f);
        }

        bool TrySpeak(bool preferShort)
        {
            ulong tid = targetClientId.Value;
            var clips = VoiceClipLibrary.GetClips(tid);
            if (clips.Count == 0) return false;

            VoiceClip pick = null;
            // 1) Kısa + son 3'te yok, 2) herhangi + son 3'te yok, 3) son 1'den farklı
            for (int pass = 0; pass < 3 && pick == null; pass++)
            {
                var pool = new List<VoiceClip>();
                for (int i = 0; i < clips.Count; i++)
                {
                    var c = clips[i];
                    if (pass == 0 && (!preferShort || c.tag != ClipTag.Short)) continue;
                    bool recent = pass < 2 ? IsRecent(c.id, 3) : IsRecent(c.id, 1);
                    if (recent) continue;
                    pool.Add(c);
                }
                if (pool.Count > 0) pick = pool[Random.Range(0, pool.Count)];
            }
            if (pick == null) return false;

            recentClips[2] = recentClips[1];
            recentClips[1] = recentClips[0];
            recentClips[0] = pick.id;
            SpeakRpc(tid, pick.id);
            return true;
        }

        bool IsRecent(int id, int depth)
        {
            for (int i = 0; i < depth; i++) if (recentClips[i] == id) return true;
            return false;
        }

        // ---------- Bakış / dönüş ----------

        void UpdateLook()
        {
            Vector3 dir;
            float targetPitch = 0f;
            if (hasLook)
            {
                Vector3 eye = transform.position + Vector3.up * (shownHeight - EyeOffsetFromTop);
                Vector3 to = lookPoint - eye;
                dir = to;
                float flat = new Vector2(to.x, to.z).magnitude;
                targetPitch = Mathf.Clamp(-Mathf.Atan2(to.y, Mathf.Max(0.01f, flat)) * Mathf.Rad2Deg, -45f, 45f);
            }
            else dir = agent.velocity;

            dir.y = 0f;
            if (dir.sqrMagnitude > 0.04f)
            {
                var want = Quaternion.LookRotation(dir.normalized, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, want, 240f * Time.deltaTime);
            }
            if (Mathf.Abs(targetPitch - pitch.Value) > 0.5f) pitch.Value = targetPitch;
        }
    }
}
