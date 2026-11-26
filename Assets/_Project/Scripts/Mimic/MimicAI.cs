using System.Collections.Generic;
using MimicGame.Items;
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

        // Eşikler
        const float ConverseRadius = 20f;   // konuşmaya yanıt
        const float FollowRadius = 18f;     // yakın oyuncuya eşlik
        const float LureRange = 30f;        // çağrı için kurbana azami uzaklık
        const float IsolationRadius = 8f;   // "yalnız" = başka oyuncu bu kadar yakında değil
        const float FarDistance = 15f;      // "uzaktaki arkadaş" eşiği
        const float SeenRange = 30f;        // kimlik değişimini engelleyen görüş mesafesi

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

        /// <summary>Taklitçinin kimliği (sesini ve rengini kullandığı oyuncu).</summary>
        public ulong TargetClientId => targetClientId.Value;

        static readonly MovementProfile neutralProfile = new MovementProfile();
        static readonly RaycastHit[] losHits = new RaycastHit[32];

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
        readonly long[] recentClips = { -1, -1, -1 };

        // Cezbetme
        Vector3 lureSpot;
        ulong lureVictim;
        float lureDeadline;
        bool lureSpoken;

        // Bakış
        bool hasLook;
        Vector3 lookPoint;
        float lookOverrideUntil;     // konuşurken kurbana dön
        ulong lookOverrideId;
        readonly List<PlayerNetwork> tmpPlayers = new List<PlayerNetwork>(8);

        // Kimlik / görünürlük
        readonly List<ulong> candidates = new List<ulong>(8);
        bool seenCache;
        float seenCacheUntil;
        float nextIdentityTry;
        ulong followVictim = ulong.MaxValue;
        float nextFollowSpeak;
        ulong pendingVictimId = ulong.MaxValue;
        float speakingUntil;
        float nextAgentTry;
        int agentFailCount;
        readonly NavMeshPath tmpPath = new NavMeshPath();

        // Tanı
        string lastReason = "-";
        string lastLoggedReason = "";
        float lastReasonLogTime = -100f;

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
            PickInitialIdentity();
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
            if (!VoiceClipLibrary.TryGet(speakerClientId, clipId, out var clip))
            {
                Debug.LogWarning("[Taklitçi] Klip bu makinede yok: konuşmacı " + speakerClientId + ", klip " + clipId);
                return;
            }
            voicePlayer.PlayClip(clip);
        }

        // ---------- Sunucu ----------

        void SetMode(Mode m, string why)
        {
            if (mode == m) return;
            Debug.Log("[Taklitçi] Durum: " + mode + " -> " + m + " (" + why + ")");
            mode = m;
        }

        void TryEnableAgent()
        {
            if (agentReady || agent == null) return;
            if (Time.time < nextAgentTry) return;
            nextAgentTry = Time.time + 1f; // her saniye tekrar dene

            if (NavMesh.SamplePosition(transform.position, out var hit, 5f, NavMesh.AllAreas))
            {
                transform.position = hit.position;
                agent.enabled = true;
                agent.updateRotation = false; // dönüşü biz yönetiriz
                agent.Warp(hit.position);
                agentReady = agent.isOnNavMesh;
                if (agentReady) Debug.Log("[Taklitçi] Ajan NavMesh üzerine yerleşti: " + hit.position);
                else Debug.LogError("[Taklitçi] Ajan açıldı ama NavMesh üzerinde değil: " + hit.position);
            }
            else
            {
                agentFailCount++;
                if (agentFailCount == 1 || agentFailCount % 10 == 0)
                    Debug.LogError("[Taklitçi] NavMeshAgent yerleştirilemedi: 5 m içinde NavMesh yok, konum " + transform.position);
            }
        }

        // ---------- Kimlik ----------

        int ColorFor(ulong clientId)
        {
            var nm = NetworkManager;
            if (nm != null && nm.IsServer)
            {
                var po = nm.SpawnManager.GetPlayerNetworkObject(clientId);
                var pn = po != null ? po.GetComponent<PlayerNetwork>() : null;
                if (pn != null) return pn.ColorIndex;
            }
            return (int)(clientId % (ulong)PlayerAppearance.Palette.Length);
        }

        void SetIdentity(ulong id)
        {
            targetClientId.Value = id;
            colorIndex.Value = ColorFor(id);
            Debug.Log("[Taklitçi] Kimlik: Oyuncu " + id + " (renk " + colorIndex.Value + ")");
        }

        // Doğuşta: kurban henüz yok; klibi olan rastgele bir oyuncu, yoksa herhangi biri
        void PickInitialIdentity()
        {
            var nm = NetworkManager;
            if (nm == null) return;
            var list = nm.ConnectedClientsList;
            var withClips = new List<ulong>();
            var any = new List<ulong>();
            for (int i = 0; i < list.Count; i++)
            {
                ulong id = list[i].ClientId;
                any.Add(id);
                if (VoiceClipLibrary.Count(id) > 0 && ClipSharing.IsAllowed(id)) withClips.Add(id);
            }
            var pool = withClips.Count > 0 ? withClips : any;
            if (pool.Count == 0) return;
            SetIdentity(pool[Random.Range(0, pool.Count)]);
        }

        void CheckRetarget()
        {
            var nm = NetworkManager;
            if (nm == null) return;
            // Kimliğin sahibi ayrıldıysa (ve kimse görmüyorsa) yenisini seç
            if (!nm.ConnectedClients.ContainsKey(targetClientId.Value) && !IsSeen()) PickInitialIdentity();
        }

        /// <summary>Herhangi bir oyuncunun 30 m içinde, kafasından taklitçinin göğsüne görüş hattı var mı?</summary>
        bool IsSeen()
        {
            if (Time.time < seenCacheUntil) return seenCache;
            seenCacheUntil = Time.time + 0.15f;
            seenCache = false;
            Vector3 chest = transform.position + Vector3.up * 1.2f;
            var all = PlayerNetwork.All;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || !p.IsSpawned) continue;
                if (Vector3.Distance(p.transform.position, transform.position) > SeenRange) continue;
                if (HasLineOfSight(Head(p), chest)) { seenCache = true; break; }
            }
            return seenCache;
        }

        /// <summary>İki nokta arası engelsiz mi? Oyuncu kapsülleri, eşyalar ve taklitçiler yok sayılır.</summary>
        public static bool HasLineOfSight(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 0.05f) return true;
            int n = Physics.RaycastNonAlloc(from, dir / dist, losHits, dist, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var col = losHits[i].collider;
                if (col == null) continue;
                if (col is CharacterController) continue;
                if (col.GetComponentInParent<Item>() != null) continue;
                if (col.GetComponentInParent<MimicAI>() != null) continue;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Kurban için kimlik seç: kurban dışındaki, klibi olan ve izin veren oyunculardan;
        /// kurbana >15 m uzaktakilerin en uzağı, yoksa rastgele (mevcut kimlik uygunsa o kalır).
        /// Aday yoksa false.
        /// </summary>
        bool ResolveIdentity(PlayerNetwork victim, out ulong id)
        {
            id = ulong.MaxValue;
            var nm = NetworkManager;
            if (nm == null || victim == null) return false;

            candidates.Clear();
            var list = nm.ConnectedClientsList;
            for (int i = 0; i < list.Count; i++)
            {
                ulong c = list[i].ClientId;
                if (c == victim.OwnerClientId) continue;
                if (VoiceClipLibrary.Count(c) == 0) continue;
                if (!ClipSharing.IsAllowed(c)) continue;
                candidates.Add(c);
            }
            if (candidates.Count == 0) return false;

            float bestD = FarDistance;
            bool hasFar = false;
            ulong far = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                var p = FindPlayer(candidates[i]);
                if (p == null) continue;
                float d = FlatDist(p.transform.position, victim.transform.position);
                if (d > bestD) { bestD = d; far = candidates[i]; hasFar = true; }
            }
            if (hasFar) { id = far; return true; }

            ulong cur = targetClientId.Value;
            id = candidates.Contains(cur) ? cur : candidates[Random.Range(0, candidates.Count)];
            return true;
        }

        /// <summary>Kimliği uygular. Kimlik değişecekse ve taklitçi görülüyorsa false (değişmez).</summary>
        bool ApplyIdentity(ulong id)
        {
            if (id == targetClientId.Value) return true;
            if (IsSeen()) return false;
            SetIdentity(id);
            return true;
        }

        // Sessiz deneme (takip sırasında renk/kimlik düzeltme)
        void TryAdoptIdentityFor(PlayerNetwork victim)
        {
            if (ResolveIdentity(victim, out ulong id)) ApplyIdentity(id);
        }

        // Hareket profili: kurbanın kendi profili asla kullanılmaz
        MovementProfile ProfileFor(PlayerNetwork victim)
        {
            ulong id = targetClientId.Value;
            if (victim != null && id == victim.OwnerClientId)
            {
                var nm = NetworkManager;
                if (nm != null)
                {
                    var list = nm.ConnectedClientsList;
                    for (int i = 0; i < list.Count; i++)
                        if (list[i].ClientId != id) return MovementProfiler.Get(list[i].ClientId);
                }
                return neutralProfile;
            }
            return MovementProfiler.Get(id);
        }

        // ---------- Sunucu döngüsü ----------

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
                CollectPlayers();
                var victim = FindPlayer(pendingVictimId) ?? NearestPlayer();
                if (now >= speakCooldownUntil)
                {
                    bool ok = TrySpeak(victim, true, "yanıt", now);
                    speakCooldownUntil = now + (ok ? Random.Range(5f, 12f) : 2f);
                }
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

        public static Vector3 Head(PlayerNetwork p)
            => p.cameraPivot != null ? p.cameraPivot.position : p.transform.position + Vector3.up * 1.6f;

        static float FlatDist(Vector3 a, Vector3 b)
        {
            a.y = 0f; b.y = 0f;
            return Vector3.Distance(a, b);
        }

        bool InsideBuilding(Vector3 p)
            => Mathf.Abs(p.x - buildingCenter.x) < buildingHalfSize.x - 0.5f &&
               Mathf.Abs(p.z - buildingCenter.z) < buildingHalfSize.y - 0.5f;

        PlayerNetwork NearestPlayer()
        {
            PlayerNetwork nearest = null;
            float best = float.MaxValue;
            for (int i = 0; i < tmpPlayers.Count; i++)
            {
                if (tmpPlayers[i] == null) continue;
                float d = FlatDist(transform.position, tmpPlayers[i].transform.position);
                if (d < best) { best = d; nearest = tmpPlayers[i]; }
            }
            return nearest;
        }

        void Decide(float now)
        {
            CollectPlayers();
            var nearest = NearestPlayer();
            float nearestD = nearest != null ? FlatDist(transform.position, nearest.transform.position) : float.MaxValue;
            var prof = ProfileFor(nearest);

            // Cezbetme sürüyorsa devam et
            if (mode == Mode.Lure)
            {
                UpdateLure(now, prof);
                return;
            }

            // Yeni cezbetme: yalnız kalmış bir oyuncu
            if (now >= lureCooldownUntil && StartLure(now))
            {
                UpdateLure(now, prof);
                return;
            }

            if (nearest != null && nearestD <= FollowRadius)
            {
                if (mode != Mode.Follow)
                {
                    SetMode(Mode.Follow, "oyuncu " + nearest.OwnerClientId + " " + nearestD.ToString("0") + " m");
                    waiting = false; hasDest = false; nextFollowPath = 0f;
                    nextFollowSpeak = now + Random.Range(6f, 12f);
                    followVictim = ulong.MaxValue;
                }
                // Kurban değişince (veya kimlik kurbanın kendisiyse) kimliği düzelt
                if (nearest.OwnerClientId != followVictim)
                {
                    followVictim = nearest.OwnerClientId;
                    nextIdentityTry = now + 1.5f;
                    TryAdoptIdentityFor(nearest);
                    prof = ProfileFor(nearest);
                }
                else if (targetClientId.Value == nearest.OwnerClientId && now >= nextIdentityTry)
                {
                    nextIdentityTry = now + 1.5f;
                    TryAdoptIdentityFor(nearest);
                    prof = ProfileFor(nearest);
                }
                UpdateFollow(now, prof, nearest);

                if (now >= nextFollowSpeak && now >= speakCooldownUntil)
                {
                    nextFollowSpeak = now + Random.Range(12f, 22f);
                    bool ok = TrySpeak(nearest, false, "takip", now);
                    speakCooldownUntil = now + (ok ? Random.Range(5f, 12f) : 2f);
                }
            }
            else
            {
                if (mode != Mode.Wander)
                {
                    SetMode(Mode.Wander, "yakında oyuncu yok");
                    waiting = false; hasDest = false;
                }
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

        // Çoğunlukla binanın odaları arasında; ara sıra oyuncuların yakınına çıkar
        void PickWanderDestination(MovementProfile prof)
        {
            Vector3 chosen = transform.position;
            bool found = false;
            bool nearPlayer = tmpPlayers.Count > 0 && Random.value < 0.3f;
            for (int i = 0; i < 10 && !found; i++)
            {
                Vector3 c;
                if (nearPlayer)
                {
                    var p = tmpPlayers[Random.Range(0, tmpPlayers.Count)];
                    if (p == null) continue;
                    Vector2 circ = Random.insideUnitCircle.normalized * Random.Range(10f, 16f);
                    c = p.transform.position + new Vector3(circ.x, 0f, circ.y);
                }
                else
                {
                    c = buildingCenter + new Vector3(
                        Random.Range(-buildingHalfSize.x + 1.5f, buildingHalfSize.x - 1.5f), 0f,
                        Random.Range(-buildingHalfSize.y + 1.5f, buildingHalfSize.y - 1.5f));
                }
                c.y = transform.position.y;
                if (!NavMesh.SamplePosition(c, out var hit, 3f, NavMesh.AllAreas)) continue;
                if (FlatDist(hit.position, transform.position) < 3f) continue;
                if (!NavMesh.CalculatePath(transform.position, hit.position, NavMesh.AllAreas, tmpPath)) continue;
                if (tmpPath.status != NavMeshPathStatus.PathComplete) continue;
                chosen = hit.position;
                found = true;
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
                if (tmpPlayers[i] == null) continue;
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
            for (int i = 0; i < tmpPlayers.Count; i++)
            {
                var victim = tmpPlayers[i];
                if (victim == null) continue;
                if (FlatDist(transform.position, victim.transform.position) > LureRange) continue;

                // Yalnız: 8 m içinde başka oyuncu yok
                bool isolated = true;
                for (int j = 0; j < tmpPlayers.Count && isolated; j++)
                {
                    if (j == i || tmpPlayers[j] == null) continue;
                    if (FlatDist(tmpPlayers[j].transform.position, victim.transform.position) <= IsolationRadius) isolated = false;
                }
                if (!isolated) continue;

                if (!ResolveIdentity(victim, out ulong id)) { Reason("aday yok (çağrı)"); continue; }
                if (!ApplyIdentity(id))
                {
                    Reason("görülüyor (çağrı)");
                    lureCooldownUntil = now + 5f;
                    return false;
                }
                if (!FindLureSpot(victim, out var spotPos)) continue;

                SetMode(Mode.Lure, "kurban " + victim.OwnerClientId + ", kimlik " + id);
                lureSpot = spotPos;
                lureVictim = victim.OwnerClientId;
                lureDeadline = now + 20f;
                lureSpoken = false;
                waiting = false;
                hasDest = false;
                crouching.Value = false;
                flashlightOn.Value = false;
                agent.speed = ProfileFor(victim).walkSpeed;
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
            for (int i = 0; i < 20; i++)
            {
                float ang = Random.value * Mathf.PI * 2f;
                float r = Random.Range(6f, 14f);
                var c = victim.transform.position + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
                if (!NavMesh.SamplePosition(c, out var hit, 3f, NavMesh.AllAreas)) continue;
                // Oyuncunun görüş hattında olmayan nokta
                if (HasLineOfSight(head, hit.position + Vector3.up * 1.2f)) continue;
                if (!NavMesh.CalculatePath(transform.position, hit.position, NavMesh.AllAreas, tmpPath)) continue;
                if (tmpPath.status != NavMeshPathStatus.PathComplete) continue;
                result = hit.position;
                return true;
            }
            return false;
        }

        void UpdateLure(float now, MovementProfile prof)
        {
            PlayerNetwork victim = FindPlayer(lureVictim);
            if (victim == null || now > lureDeadline) { EndLure(now); return; }

            hasLook = true;
            lookPoint = Head(victim);

            bool arrived = FlatDist(transform.position, lureSpot) < 1.5f ||
                (!agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.3f);
            if (arrived && !lureSpoken)
            {
                // Görülüyorsa konuşmaz; süre dolana kadar her turda tekrar dener
                if (TrySpeak(victim, true, "çağrı", now))
                {
                    lureSpoken = true;
                    speakCooldownUntil = now + Random.Range(5f, 12f);
                    lureDeadline = now + 4f;
                }
            }
        }

        void EndLure(float now)
        {
            SetMode(Mode.Wander, "çağrı bitti");
            waiting = false;
            hasDest = false;
            hasLook = false;
            lureCooldownUntil = now + Random.Range(15f, 30f);
            agent.ResetPath();
        }

        PlayerNetwork FindPlayer(ulong clientId)
        {
            for (int i = 0; i < tmpPlayers.Count; i++)
                if (tmpPlayers[i] != null && tmpPlayers[i].OwnerClientId == clientId) return tmpPlayers[i];
            return null;
        }

        // ---- Konuşma tetikleri ----

        void OnSpeechEnded(ulong clientId, Vector3 speakerPos)
        {
            if (!IsServer || !IsSpawned) return;
            if (FlatDist(transform.position, speakerPos) > ConverseRadius) return;
            if (pendingSpeakAt > 0f || Time.time < speakCooldownUntil) return;
            pendingVictimId = clientId;
            pendingSpeakAt = Time.time + Random.Range(0.6f, 1.8f);
        }

        void Reason(string r)
        {
            lastReason = r;
            if (r != lastLoggedReason || Time.time - lastReasonLogTime > 5f)
            {
                lastLoggedReason = r;
                lastReasonLogTime = Time.time;
                Debug.Log("[Taklitçi] Konuşamıyor: " + r);
            }
        }

        bool TrySpeak(PlayerNetwork victim, bool preferShort, string ctx, float now)
        {
            if (victim == null) { Reason("kurban yok"); return false; }
            if (now < speakingUntil) { Reason("zaten konuşuyor"); return false; }
            if (!ResolveIdentity(victim, out ulong id)) { Reason("aday yok"); return false; }
            if (!ApplyIdentity(id)) { Reason("görülüyor"); return false; }

            var clips = VoiceClipLibrary.GetClips(id);
            if (clips.Count == 0) { Reason("klip yok"); return false; }

            VoiceClip pick = null;
            // 1) Kısa + son 3'te yok, 2) herhangi + son 3'te yok, 3) herhangi
            for (int pass = 0; pass < 3 && pick == null; pass++)
            {
                var pool = new List<VoiceClip>();
                for (int i = 0; i < clips.Count; i++)
                {
                    var c = clips[i];
                    if (pass == 0 && (!preferShort || c.tag != ClipTag.Short)) continue;
                    if (pass < 2 && IsRecent(id, c.id)) continue;
                    pool.Add(c);
                }
                if (pool.Count > 0) pick = pool[Random.Range(0, pool.Count)];
            }
            if (pick == null) { Reason("klip yok"); return false; }

            recentClips[2] = recentClips[1];
            recentClips[1] = recentClips[0];
            recentClips[0] = ClipKey(id, pick.id);
            speakingUntil = now + pick.duration + 0.3f;
            lookOverrideId = victim.OwnerClientId;
            lookOverrideUntil = now + pick.duration + 0.5f;
            lastReason = "konuştu: " + ctx + ", klip " + pick.id + ", kurban " + victim.OwnerClientId;
            Debug.Log("[Taklitçi] Konuşuyor: kimlik " + id + ", klip " + pick.id + ", kurban " + victim.OwnerClientId + " (" + ctx + ", " + pick.duration.ToString("0.0") + " sn)");
            SpeakRpc(id, pick.id);
            return true;
        }

        static long ClipKey(ulong speaker, int clipId) => ((long)(speaker & 0xFFFFFF) << 32) | (uint)clipId;

        bool IsRecent(ulong speaker, int clipId)
        {
            long key = ClipKey(speaker, clipId);
            for (int i = 0; i < recentClips.Length; i++) if (recentClips[i] == key) return true;
            return false;
        }

        // ---------- Tanı ----------

        public string DebugText()
        {
            return mode + " | kimlik: Oyuncu " + targetClientId.Value + " (renk " + colorIndex.Value + ") | ajan: " + (agentReady ? "hazır" : "YOK")
                + "\n    son konuşma: " + lastReason
                + "\n    konum: " + transform.position.ToString("F1") + (IsSeen() ? " | görülüyor" : " | görülmüyor");
        }

        // ---------- Bakış / dönüş ----------

        void UpdateLook()
        {
            bool look = hasLook;
            Vector3 lp = lookPoint;
            // Konuşurken yürümeye devam eder ama kurbana döner
            if (Time.time < lookOverrideUntil)
            {
                var v = FindPlayer(lookOverrideId);
                if (v != null) { look = true; lp = Head(v); }
            }

            Vector3 dir;
            float targetPitch = 0f;
            if (look)
            {
                Vector3 eye = transform.position + Vector3.up * (shownHeight - EyeOffsetFromTop);
                Vector3 to = lp - eye;
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
