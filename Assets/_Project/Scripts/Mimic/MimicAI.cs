using System.Collections.Generic;
using MimicGame.Environment;
using MimicGame.Items;
using MimicGame.Level;
using MimicGame.Network;
using MimicGame.Player;
using MimicGame.Voice;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.AI;

namespace MimicGame.Mimic
{
    /// <summary>
    /// Taklitçi: seçtiği oyuncunun görünümünü, hareket alışkanlıklarını ve sesini kopyalar.
    /// Sunucu yetkili; istemciler yalnızca görünümü uygular ve konuşma RPC'sini çalar.
    /// Uygun anda gerçek biçimini açığa çıkarıp kurbanı kovalar ve öldürür.
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
        const float ApproachStop = 2.8f;    // oyuncunun yanında durma mesafesi
        const float LureRange = 30f;        // çağrı için kurbana azami uzaklık
        const float IsolationRadius = 8f;   // "yalnız" = başka oyuncu bu kadar yakında değil
        const float FarDistance = 15f;      // "uzaktaki arkadaş" eşiği
        const float SeenRange = 30f;        // kimlik değişimini engelleyen görüş mesafesi
        const float IdentityHold = 20f;     // kimliği en az bu kadar sn koru

        // Saldırı
        const float AttackIsolatedRange = 3f;     // yalnız kurbana bu kadar yakın >= 2 sn
        const float AttackIsolatedTime = 2f;
        const float AttackBehindRange = 2.2f;     // arkasında >= 1.2 sn
        const float AttackBehindTime = 1.2f;
        const float AttackBehindAngle = 110f;
        const float WindUpTime = 0.6f;
        const float ChaseSpeed = 5.4f;
        const float ChaseAcceleration = 60f;
        const float ChaseMaxTime = 12f;
        const float ChaseGiveUpDistance = 25f;
        const float ChaseLostSightTime = 3f;
        const float KillRange = 1.4f;
        const float PostKillHold = 2f;
        const float FleeMinDistance = 15f;
        const float RevealHeight = 2.6f;
        const float RevealWidth = 0.55f;
        const float RevealScreamRate = 0.65f;
        static readonly Color RevealColor = new Color(0.12f, 0.02f, 0.02f);

        const float TurnRate = 360f;        // gövde dönüş hızı (°/sn)

        enum Activity { Explore, Group, Idle, Lure }
        enum ExploreState { Pick, Walk, DoorWait, Look }
        static readonly string[] ActivityNames = { "Keşif", "Grup", "Bekleme", "Çağrı" };

        // Dış mekân ilgi noktaları (TestSceneBuilder'daki araç, lamba direkleri, çanak/direk, cephe çevresi)
        static readonly Vector3[] OutdoorPois =
        {
            new Vector3(3.6f, 0f, -8f), new Vector3(-3.6f, 0f, -8f), new Vector3(0f, 0f, -12.5f),
            new Vector3(-4.5f, 0f, -4f), new Vector3(5.5f, 0f, -12f), new Vector3(-4f, 0f, 3f), new Vector3(4f, 0f, 3f),
            new Vector3(-4.5f, 0f, 8f), new Vector3(4.5f, 0f, 8f), new Vector3(0f, 0f, 1.75f),
            new Vector3(-6f, 0f, 34f), new Vector3(11f, 0f, 34f), new Vector3(-13f, 0f, 20f), new Vector3(13f, 0f, 20f),
            new Vector3(0f, 0f, 33f), new Vector3(-13f, 0f, 12f), new Vector3(13f, 0f, 12f)
        };
        enum AttackState { None, WindUp, Chase, PostKill, Flee }
        enum ClipPref { Call, Reply }

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
        // Gerçek biçim açığa çıktı mı (saldırı boyunca)
        readonly NetworkVariable<bool> revealed = new NetworkVariable<bool>(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Taklitçinin kimliği (sesini ve rengini kullandığı oyuncu).</summary>
        public ulong TargetClientId => targetClientId.Value;
        public bool Revealed => revealed.Value;

        static readonly MovementProfile neutralProfile = new MovementProfile();
        static readonly RaycastHit[] losHits = new RaycastHit[32];

        NavMeshAgent agent;
        float shownHeight = StandHeight;
        float shownPitch;

        // ---- Sunucu durumu ----
        Activity activity = Activity.Explore;
        bool agentReady;
        float nextDecision, nextRetarget;
        NetworkTransform netTransform;

        // Keşif
        ExploreState exState = ExploreState.Pick;
        Vector3 exDest;
        bool exDestInside;
        bool exViaDoor;
        float exTimeout, nextPickAt, doorWaitUntil, nextRepath;
        float lookEnd, nextLookStep;
        readonly List<int> recentRooms = new List<int>(6);
        Vector3 avoidPoint;
        float avoidUntil;
        int zonePlanKind;                     // 0 yok, 1 dışarı çık, 2 içeri gir
        float zonePlanAt = float.MaxValue;

        // Grup
        ulong groupTarget = ulong.MaxValue;
        float groupStart, groupUntil, groupCooldownUntil, groupDist, groupAngle;
        float nextGroupPath, nextGroupLook, nextGroupToggle, nextGroupCheck, nextGroupCrouch;
        bool groupApproaching;

        // Bekleme
        float idleUntil, nextIdleLook;

        // Hareket hissi ve bakış
        float desiredSpeed = 3.5f;
        float pauseUntil;
        int lastPauseDoor = -1;
        float serverPitch;
        bool freeLookOn;
        float freeLookYaw, freeLookPitch;

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
        float identitySince;
        ulong followVictim = ulong.MaxValue;
        float nextFollowSpeak;
        ulong pendingVictimId = ulong.MaxValue;
        float speakingUntil;
        float nextAgentTry;
        int agentFailCount;
        NavMeshPath tmpPath; // Awake içinde oluşturulur (alan başlatıcısında yasak)

        // Saldırı (sunucu)
        AttackState atk = AttackState.None;
        float attackCooldownUntil;
        float lastTriggerTick;
        ulong isoVictim = ulong.MaxValue, behindVictim = ulong.MaxValue, followTimeVictim = ulong.MaxValue;
        float isoTimer, behindTimer, followTime, followLimit;
        ulong atkVictim;
        float atkStart, chaseStart, lastSeenVictim, nextChasePath, nextSightCheck;
        string atkReason = "-";
        float baseAcceleration = 20f, baseStopping = 0.5f;
        Vector3 fleeDest;
        bool fleeHasDest;
        float fleeStart, fleeAttemptStart;
        int fleeRetries;

        // Açığa çıkma görünümü (her istemcide)
        float revealT;

        // Tanı
        string lastReason = "-";
        string lastLoggedReason = "";
        float lastReasonLogTime = -100f;

        void Awake()
        {
            tmpPath = new NavMeshPath();
            netTransform = GetComponent<NetworkTransform>();
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
            revealed.OnValueChanged += OnRevealedChanged;
            ApplyBodyColor();
            revealT = revealed.Value ? 1f : 0f;
            shownHeight = crouching.Value ? CrouchHeight : StandHeight;
            shownPitch = pitch.Value;
            if (spot != null) spot.enabled = flashlightOn.Value;

            if (!IsServer) return;
            SpeechTracker.SpeechEnded += OnSpeechEnded;
            PickInitialIdentity();
            nextRetarget = Time.time + 30f;
            speakCooldownUntil = Time.time + Random.Range(4f, 8f);
            attackCooldownUntil = Time.time + Random.Range(25f, 45f);
            lureCooldownUntil = Time.time + Random.Range(10f, 20f);
            groupCooldownUntil = Time.time + Random.Range(8f, 20f);
            nextPickAt = Time.time;
            TryEnableAgent();
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            colorIndex.OnValueChanged -= OnColorChanged;
            revealed.OnValueChanged -= OnRevealedChanged;
            SpeechTracker.SpeechEnded -= OnSpeechEnded;
        }

        void OnColorChanged(int oldValue, int newValue) => ApplyBodyColor();
        void OnRevealedChanged(bool oldValue, bool newValue) => ApplyBodyColor();

        // Açıkken kan kırmızısına yakın siyah, değilken kimliğin rengi
        void ApplyBodyColor()
        {
            if (revealed.Value) PlayerAppearance.ApplyColor(bodyRenderer, RevealColor);
            else PlayerAppearance.ApplyColor(bodyRenderer, colorIndex.Value);
        }

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
            revealT = Mathf.MoveTowards(revealT, revealed.Value ? 1f : 0f, Time.deltaTime / 0.3f);
            float bodyH = Mathf.Lerp(shownHeight, RevealHeight, revealT);
            float bodyW = Mathf.Lerp(0.7f, RevealWidth, revealT);
            PlayerAppearance.ApplyBody(body, bodyH, bodyW);
            // Açıkken gövde titrer (±2 cm)
            if (revealT > 0.01f && body != null)
                body.localPosition += Random.insideUnitSphere * (0.02f * revealT);
            if (cameraPivot != null)
            {
                cameraPivot.localPosition = new Vector3(0f, shownHeight - EyeOffsetFromTop, 0f);
                cameraPivot.localRotation = Quaternion.Euler(shownPitch, 0f, 0f);
            }
            if (spot != null) spot.enabled = flashlightOn.Value && !revealed.Value;
        }

        // ---------- Konuşma ----------

        // Klip paketleri RPC ile birlikte gider (Reliable = parçalı+sıralı, boyut sınırı yok);
        // böylece her istemci klibi önbelleğe ihtiyaç duymadan çalar.
        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Reliable, InvokePermission = RpcInvokePermission.Server)]
        void SpeakRpc(ulong speakerClientId, int clipId, byte[] blob)
        {
            if (voicePlayer == null) return;
            if (!TryParseBlob(blob, speakerClientId, clipId, out var clip))
            {
                Debug.LogWarning("[Taklitçi] Klip verisi çözülemedi: konuşmacı " + speakerClientId + ", klip " + clipId);
                return;
            }
            voicePlayer.PlayClip(clip);
        }

        // Açığa çıkarken: kimliğin bir klibi yavaş (0.65x) ve kalın "çığlık" olarak çalınır
        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Reliable, InvokePermission = RpcInvokePermission.Server)]
        void RevealRpc(ulong speakerClientId, int clipId, byte[] blob)
        {
            if (voicePlayer == null || blob == null || blob.Length == 0) return;
            if (!TryParseBlob(blob, speakerClientId, clipId, out var clip)) return;
            voicePlayer.Interrupt(); // çalan normal konuşmayı kes
            voicePlayer.PlayClip(clip, RevealScreamRate);
        }

        // blob = her paket için [codec(1) | uzunluk(ushort LE) | bayt...]
        static byte[] BuildBlob(VoiceClip clip)
        {
            int total = 0;
            var pk = clip.packets;
            for (int i = 0; i < pk.Count; i++)
            {
                var d = pk[i].data;
                if (d == null || d.Length == 0 || d.Length > ushort.MaxValue) continue;
                total += 3 + d.Length;
            }
            var blob = new byte[total];
            int o = 0;
            for (int i = 0; i < pk.Count; i++)
            {
                var d = pk[i].data;
                if (d == null || d.Length == 0 || d.Length > ushort.MaxValue) continue;
                blob[o++] = pk[i].codec;
                blob[o++] = (byte)(d.Length & 0xFF);
                blob[o++] = (byte)((d.Length >> 8) & 0xFF);
                System.Buffer.BlockCopy(d, 0, blob, o, d.Length);
                o += d.Length;
            }
            return blob;
        }

        static bool TryParseBlob(byte[] blob, ulong speaker, int clipId, out VoiceClip clip)
        {
            clip = null;
            if (blob == null || blob.Length < 4) return false;
            var packets = new List<(byte codec, byte[] data)>(64);
            int o = 0;
            while (o + 3 <= blob.Length)
            {
                byte codec = blob[o];
                int len = blob[o + 1] | (blob[o + 2] << 8);
                o += 3;
                if (len <= 0 || o + len > blob.Length) break; // bozuk/kesik veri
                var data = new byte[len];
                System.Buffer.BlockCopy(blob, o, data, 0, len);
                o += len;
                packets.Add((codec, data));
            }
            if (packets.Count == 0) return false;
            clip = new VoiceClip { speakerClientId = speaker, id = clipId, packets = packets };
            return true;
        }

        // ---------- Sunucu ----------

        void SetActivity(Activity a, string why)
        {
            if (activity == a) return;
            Debug.Log("[Taklitçi] Etkinlik: " + ActivityNames[(int)activity] + " -> " + ActivityNames[(int)a] + " (" + why + ")");
            if (activity == Activity.Group)
            {
                groupTarget = ulong.MaxValue;
                groupCooldownUntil = Mathf.Max(groupCooldownUntil, Time.time + Random.Range(30f, 60f));
                followTime = 0f;
                followTimeVictim = ulong.MaxValue;
            }
            activity = a;
            freeLookOn = false;
            pauseUntil = 0f;
            if (agentReady && agent.isOnNavMesh) agent.isStopped = false;
        }

        /// <summary>F9 paneli için kısa etkinlik adı.</summary>
        public string ActivityLabel => ActivityNames[(int)activity] + (activity == Activity.Explore ? "/" + exState : "");
        /// <summary>Grup etkinliğinde eşlik ettiği oyuncu (yoksa ulong.MaxValue).</summary>
        public ulong GroupTargetId => activity == Activity.Group ? groupTarget : ulong.MaxValue;

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
                baseAcceleration = agent.acceleration;
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
            identitySince = Time.time;
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
            // Başka taklitçilerin kullanmadığı kimlikleri tercih et
            var free = new List<ulong>(pool.Count);
            for (int i = 0; i < pool.Count; i++) if (!IdentityUsedByOther(pool[i])) free.Add(pool[i]);
            if (free.Count > 0) pool = free;
            SetIdentity(pool[Random.Range(0, pool.Count)]);
        }

        bool IdentityUsedByOther(ulong id)
        {
            for (int i = 0; i < All.Count; i++)
            {
                var m = All[i];
                if (m != null && m != this && m.IsSpawned && m.targetClientId.Value == id) return true;
            }
            return false;
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
                if (p == null || !p.IsSpawned || p.IsDead) continue;
                if (p.IsInside != SelfInside) continue; // farklı bölgedekiler görmez
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

            // Başka taklitçilerin kullanmadığı kimlikler tercih edilir
            if (candidates.Count > 1)
            {
                var unused = new List<ulong>(candidates.Count);
                for (int i = 0; i < candidates.Count; i++) if (!IdentityUsedByOther(candidates[i])) unused.Add(candidates[i]);
                if (unused.Count > 0) { candidates.Clear(); candidates.AddRange(unused); }
            }

            // Histerezis: kimlik uygunsa ve yeni değiştiyse koru (kurbanın kendisi adaylarda zaten yok)
            ulong cur = targetClientId.Value;
            if (Time.time - identitySince < IdentityHold && candidates.Contains(cur)) { id = cur; return true; }

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

            // Saldırı sürüyorsa normal davranış durur
            if (atk != AttackState.None)
            {
                pendingSpeakAt = -1f;
                UpdateAttack(now);
                UpdateLook();
                return;
            }

            if (pendingSpeakAt > 0f && now >= pendingSpeakAt)
            {
                pendingSpeakAt = -1f;
                CollectPlayers();
                var victim = FindPlayer(pendingVictimId) ?? NearestPlayer();
                if (now >= speakCooldownUntil)
                {
                    bool ok = TrySpeak(victim, ConverseRadius, false, ClipPref.Reply, "yanıt", now);
                    speakCooldownUntil = now + (ok ? Random.Range(5f, 12f) : 2f);
                }
            }

            if (now >= nextDecision)
            {
                nextDecision = now + 0.25f;
                Decide(now);
            }

            UpdateMovementFeel(now, Time.deltaTime);
            UpdateLook();
        }

        void CollectPlayers()
        {
            tmpPlayers.Clear();
            var all = PlayerNetwork.All;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].IsSpawned && !all[i].IsDead && all[i].IsInside == SelfInside)
                    tmpPlayers.Add(all[i]); // yalnızca aynı bölgedeki (içeri/dışarı) oyuncular hedef olur
        }

        public static Vector3 Head(PlayerNetwork p)
            => p.cameraPivot != null ? p.cameraPivot.position : p.transform.position + Vector3.up * 1.6f;

        // Yatay uzaklık; farklı katlardaki (dikey fark > 2 m) noktalar uzak sayılır
        static float FlatDist(Vector3 a, Vector3 b)
        {
            float dy = Mathf.Abs(a.y - b.y);
            a.y = 0f; b.y = 0f;
            return Vector3.Distance(a, b) + (dy > 2f ? dy * 5f : 0f);
        }

        // Taklitçi tesisin içinde mi (iç mekân haritanın çok altındadır)
        bool SelfInside => FacilityGenerator.IsInteriorPoint(transform.position);

        bool InsideBuilding(Vector3 p)
            => FacilityGenerator.IsInteriorPoint(p) || Mathf.Abs(p.x - buildingCenter.x) < buildingHalfSize.x - 0.5f &&
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

            // Saldırı tetikleri (bekleme bittiyse)
            if (CheckAttackTriggers(now, nearest, nearestD)) return;

            UpdateZonePlan(now);

            // Cezbetme sürüyorsa devam et
            if (activity == Activity.Lure)
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

            // Bölge değiştirme zamanı geldiyse gruptan / beklemeden çık
            if (PlanDue(now) && activity != Activity.Explore)
            {
                if (activity == Activity.Group) EndGroup(now, "bölge planı");
                else EndIdle(now, "bölge planı");
            }

            switch (activity)
            {
                case Activity.Explore: UpdateExplore(now, prof); break;
                case Activity.Group: UpdateGroup(now); break;
                case Activity.Idle: UpdateIdle(now); break;
            }
        }

        // ---- Bölge planı (içeri / dışarı) ----

        bool DoorsAvailable()
            => FacilityGenerator.Generated && FacilityDoor.Exterior != null && FacilityDoor.Interior != null;

        bool PlanDue(float now) => zonePlanKind != 0 && now >= zonePlanAt && DoorsAvailable();

        // Tüm canlı oyuncular dışarıdaysa tesisteki taklitçi (%70) 20-60 sn içinde çıkar;
        // hepsi içerideyse dışarıdaki taklitçi kısa sürede içeri döner.
        void UpdateZonePlan(float now)
        {
            int inCount = 0, outCount = 0;
            var all = PlayerNetwork.All;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || !p.IsSpawned || p.IsDead) continue;
                if (p.IsInside) inCount++; else outCount++;
            }
            int want = 0;
            if (inCount + outCount > 0)
            {
                if (SelfInside && inCount == 0 && outCount > 0) want = 1;
                else if (!SelfInside && outCount == 0 && inCount > 0) want = 2;
            }
            if (want == zonePlanKind) return;
            zonePlanKind = want;
            if (want == 1) zonePlanAt = Random.value < 0.7f ? now + Random.Range(20f, 60f) : float.MaxValue;
            else if (want == 2) zonePlanAt = now + Random.Range(6f, 18f);
            else zonePlanAt = float.MaxValue;
            if (want != 0)
                Debug.Log("[Taklitçi] Bölge planı: " + (want == 1 ? "dışarı çıkacak" : "içeri dönecek") +
                    (zonePlanAt < float.MaxValue ? " (" + (zonePlanAt - now).ToString("0") + " sn sonra)" : " (vazgeçti)"));
        }

        // ---- Keşif ----

        void UpdateExplore(float now, MovementProfile prof)
        {
            bool canJoin = exState == ExploreState.Pick || (exState == ExploreState.Walk && !exViaDoor);
            if (canJoin && TryJoinGroup(now, 0.12f)) return;

            switch (exState)
            {
                case ExploreState.Pick:
                    if (now >= nextPickAt) PickExploreDestination(now, prof);
                    break;
                case ExploreState.Walk: UpdateWalk(now, prof); break;
                case ExploreState.DoorWait: UpdateDoorWait(now, prof); break;
                case ExploreState.Look: UpdateLookAround(now, prof); break;
            }
        }

        void PickExploreDestination(float now, MovementProfile prof)
        {
            bool doors = DoorsAvailable();
            bool destInside = SelfInside;
            if (doors)
            {
                if (PlanDue(now)) destInside = zonePlanKind == 2;
                else if (Random.value < 0.15f) destInside = !SelfInside; // gezerken %15 öteki bölgeyi seç
            }

            Vector3 dest;
            bool found = destInside ? PickRoomPoint(out dest) : PickOutdoorPoint(out dest);
            if (!found && destInside != SelfInside)
            {
                destInside = SelfInside;
                found = destInside ? PickRoomPoint(out dest) : PickOutdoorPoint(out dest);
            }
            if (!found) { nextPickAt = now + 1.5f; return; }

            exDest = dest;
            exDestInside = destInside;
            BeginWalk(now, prof);
        }

        // Yakın zamanda gezilmemiş bir oda seç (merkez civarı rastgele nokta)
        bool PickRoomPoint(out Vector3 dest)
        {
            dest = default;
            var rooms = FacilityGenerator.Rooms;
            if (!FacilityGenerator.Generated || rooms.Count == 0) return false;
            for (int i = 0; i < 16; i++)
            {
                int idx = Random.Range(0, rooms.Count);
                var r = rooms[idx];
                if (r.kind == RoomKind.Stair) continue;
                if (i < 12 && recentRooms.Contains(idx)) continue;
                Vector3 ext = r.bounds.extents;
                float ex = Mathf.Max(0.4f, ext.x - 1.3f), ez = Mathf.Max(0.4f, ext.z - 1.3f);
                var c = new Vector3(r.bounds.center.x + Random.Range(-ex, ex), r.center.y, r.bounds.center.z + Random.Range(-ez, ez));
                if (Time.time < avoidUntil && FlatDist(c, avoidPoint) < 12f) continue;
                if (!NavMesh.SamplePosition(c, out var hit, 3f, NavMesh.AllAreas)) continue;
                if (Mathf.Abs(hit.position.y - r.center.y) > 1.5f) continue;
                if (SelfInside)
                {
                    if (Vector3.Distance(hit.position, transform.position) < 3f) continue;
                    if (!NavMesh.CalculatePath(transform.position, hit.position, NavMesh.AllAreas, tmpPath)) continue;
                    if (tmpPath.status != NavMeshPathStatus.PathComplete) continue;
                }
                dest = hit.position;
                recentRooms.Add(idx);
                if (recentRooms.Count > 4) recentRooms.RemoveAt(0);
                return true;
            }
            return false;
        }

        bool PickOutdoorPoint(out Vector3 dest)
        {
            dest = default;
            for (int i = 0; i < 10; i++)
            {
                Vector3 c = OutdoorPois[Random.Range(0, OutdoorPois.Length)] + new Vector3(Random.Range(-2f, 2f), 0f, Random.Range(-2f, 2f));
                if (!NavMesh.SamplePosition(c, out var hit, 3f, NavMesh.AllAreas)) continue;
                if (Time.time < avoidUntil && FlatDist(hit.position, avoidPoint) < 12f) continue;
                if (!SelfInside)
                {
                    if (FlatDist(hit.position, transform.position) < 3f) continue;
                    if (!NavMesh.CalculatePath(transform.position, hit.position, NavMesh.AllAreas, tmpPath)) continue;
                    if (tmpPath.status != NavMeshPathStatus.PathComplete) continue;
                }
                dest = hit.position;
                return true;
            }
            return false;
        }

        void BeginWalk(float now, MovementProfile prof)
        {
            exState = ExploreState.Walk;
            exTimeout = now + 70f;
            bool sprint = Random.value < Mathf.Min(0.35f, prof.sprintRatio * 1.5f);
            bool crouch = !sprint && Random.value < prof.crouchRatio * 0.5f;
            float walk = Mathf.Clamp(prof.walkSpeed, 1.8f, 4.5f);
            desiredSpeed = crouch ? 1.8f : (sprint ? Mathf.Clamp(prof.sprintSpeed, 4.6f, 6.5f) : walk);
            crouching.Value = crouch;
            flashlightOn.Value = WantsFlashlight(exDest, prof);
            hasLook = false;
            freeLookOn = false;
            agent.stoppingDistance = 0.3f;
            agent.isStopped = false;
            NavigateToGoal();
        }

        // Hedef öteki bölgedeyse kendi bölgesindeki kapının önüne yürür
        void NavigateToGoal()
        {
            exViaDoor = false;
            if (DoorsAvailable() && SelfInside != exDestInside)
            {
                var door = SelfInside ? FacilityDoor.Interior : FacilityDoor.Exterior;
                Vector3 front = door.FrontPosition;
                if (NavMesh.SamplePosition(front, out var hit, 2.5f, NavMesh.AllAreas)) front = hit.position;
                exViaDoor = true;
                agent.SetDestination(front);
            }
            else agent.SetDestination(exDest);
        }

        void UpdateWalk(float now, MovementProfile prof)
        {
            if (now > exTimeout) { agent.ResetPath(); exState = ExploreState.Pick; nextPickAt = now; return; }
            // Bölge planı vaktiyse yürüyüşü bırakıp öteki bölgeye yeni hedef seç
            if (!exViaDoor && exDestInside == SelfInside && PlanDue(now)) { exState = ExploreState.Pick; nextPickAt = now; return; }
            if (agent.pathPending) return;
            if (agent.pathStatus == NavMeshPathStatus.PathInvalid) { agent.ResetPath(); exState = ExploreState.Pick; nextPickAt = now + 0.5f; return; }
            if (!agent.hasPath)
            {
                // Yol kayboldu: yeniden dene; zaten hedefe yakınsa varmış say
                Vector3 goal = exViaDoor ? GoalDoorFront() : exDest;
                if (FlatDist(transform.position, goal) > 1.6f)
                {
                    if (now >= nextRepath) { nextRepath = now + 1f; NavigateToGoal(); }
                    return;
                }
            }

            bool arrived;
            if (exViaDoor)
            {
                Vector3 front = GoalDoorFront();
                arrived = FlatDist(transform.position, front) < 1.4f ||
                    (agent.hasPath && agent.remainingDistance <= agent.stoppingDistance + 0.2f);
            }
            else
                arrived = !agent.hasPath || agent.remainingDistance <= agent.stoppingDistance + 0.2f;
            if (!arrived) return;

            if (exViaDoor)
            {
                var door = SelfInside ? FacilityDoor.Interior : FacilityDoor.Exterior;
                agent.ResetPath();
                exState = ExploreState.DoorWait;
                doorWaitUntil = now + 0.5f;     // kapıyı "kullanmadan" önce yarım saniye durur
                hasLook = door != null;
                if (door != null) lookPoint = door.transform.position + Vector3.up * 1.5f;
            }
            else BeginLookAround(now, prof);
        }

        Vector3 GoalDoorFront()
        {
            var door = SelfInside ? FacilityDoor.Interior : FacilityDoor.Exterior;
            return door != null ? door.FrontPosition : transform.position;
        }

        void UpdateDoorWait(float now, MovementProfile prof)
        {
            if (now < doorWaitUntil) return;
            DoDoorWarp(now, prof);
        }

        // Kapıdan geçiş: öteki kapının önüne ışınlan, kapıdan uzağa bak, yola devam et (anında, kararma yok)
        void DoDoorWarp(float now, MovementProfile prof)
        {
            bool wasInside = SelfInside;
            var to = wasInside ? FacilityDoor.Exterior : FacilityDoor.Interior;
            if (to == null) { exState = ExploreState.Pick; nextPickAt = now; return; }

            Vector3 target = to.FrontPosition;
            if (NavMesh.SamplePosition(target, out var hit, 2.5f, NavMesh.AllAreas)) target = hit.position;
            agent.ResetPath();
            if (!agent.Warp(target))
            {
                Debug.LogWarning("[Taklitçi] Kapıdan geçiş başarısız: " + target);
                exState = ExploreState.Pick;
                nextPickAt = now + 1f;
                return;
            }
            transform.rotation = Quaternion.LookRotation(to.FrontDirection, Vector3.up); // kapıya sırtı dönük
            if (netTransform != null && netTransform.CanCommitToTransform)
                netTransform.Teleport(transform.position, transform.rotation, transform.localScale);
            Debug.Log("[Taklitçi] Kapıdan geçti: " + (wasInside ? "tesisten dışarı" : "tesise içeri") + " " + target.ToString("F1"));

            hasLook = false;
            seenCacheUntil = 0f;
            exViaDoor = false;
            agent.isStopped = false;
            flashlightOn.Value = WantsFlashlight(exDest, prof);
            exState = ExploreState.Walk;
            exTimeout = now + 70f;
            agent.SetDestination(exDest);
        }

        // Varınca etrafa bakınır: 2-6 sn, yaw adımlarla döner, pitch değişir; bazen çömelir
        void BeginLookAround(float now, MovementProfile prof)
        {
            exState = ExploreState.Look;
            agent.ResetPath();
            lookEnd = now + Random.Range(2f, 6f);
            nextLookStep = now + Random.Range(0.3f, 0.9f);
            freeLookOn = true;
            freeLookYaw = transform.eulerAngles.y;
            freeLookPitch = 0f;
            hasLook = false;
            crouching.Value = Random.value < Mathf.Clamp(prof.crouchRatio, 0.05f, 0.5f);
            flashlightOn.Value = WantsFlashlight(transform.position, prof);
        }

        void UpdateLookAround(float now, MovementProfile prof)
        {
            if (now >= nextLookStep)
            {
                nextLookStep = now + Random.Range(0.7f, 1.6f);
                freeLookYaw += Random.Range(-110f, 110f);
                // yukarı raflar (negatif) ya da köşe / zemin (pozitif)
                freeLookPitch = Random.value < 0.35f ? Random.Range(-25f, -8f) : Random.Range(5f, 28f);
            }
            if (now < lookEnd) return;

            freeLookOn = false;
            if (crouching.Value) crouching.Value = false;
            float r = Random.value;
            var g = FindGroupTarget();
            if (g != null && now >= groupCooldownUntil && !PlanDue(now) && r < 0.30f) { StartGroup(g, now); return; }
            if (r < 0.42f) { StartIdle(now); return; }
            exState = ExploreState.Pick;
            nextPickAt = now;
        }

        // ---- Bekleme ----

        void StartIdle(float now)
        {
            SetActivity(Activity.Idle, "kısa bekleme");
            idleUntil = now + Random.Range(3f, 8f);
            nextIdleLook = now + Random.Range(0.8f, 2f);
            agent.ResetPath();
            hasLook = false;
            freeLookOn = true;
            freeLookYaw = transform.eulerAngles.y;
            freeLookPitch = 0f;
        }

        void UpdateIdle(float now)
        {
            if (now >= nextIdleLook)
            {
                nextIdleLook = now + Random.Range(1f, 2.5f);
                freeLookYaw += Random.Range(-40f, 40f);
                freeLookPitch = Random.Range(-8f, 12f);
            }
            if (now >= idleUntil) EndIdle(now, "süre doldu");
        }

        void EndIdle(float now, string why)
        {
            exState = ExploreState.Pick;
            nextPickAt = now;
            SetActivity(Activity.Explore, why);
        }

        // ---- Gruba katılma ----

        bool ClaimedByOther(ulong id)
        {
            for (int i = 0; i < All.Count; i++)
            {
                var m = All[i];
                if (m != null && m != this && m.activity == Activity.Group && m.groupTarget == id) return true;
            }
            return false;
        }

        PlayerNetwork FindGroupTarget()
        {
            PlayerNetwork best = null;
            float bestD = FollowRadius;
            for (int i = 0; i < tmpPlayers.Count; i++)
            {
                var p = tmpPlayers[i];
                if (p == null || ClaimedByOther(p.OwnerClientId)) continue;
                float d = FlatDist(transform.position, p.transform.position);
                if (d <= bestD) { bestD = d; best = p; }
            }
            return best;
        }

        bool TryJoinGroup(float now, float chance)
        {
            if (now < nextGroupCheck) return false;
            nextGroupCheck = now + 1.5f;
            if (now < groupCooldownUntil || PlanDue(now)) return false;
            var target = FindGroupTarget();
            if (target == null || Random.value > chance) return false;
            StartGroup(target, now);
            return true;
        }

        void StartGroup(PlayerNetwork p, float now)
        {
            SetActivity(Activity.Group, "oyuncu " + p.OwnerClientId + " " + FlatDist(transform.position, p.transform.position).ToString("0") + " m");
            groupTarget = p.OwnerClientId;
            groupStart = now;
            groupUntil = now + Random.Range(15f, 40f);
            groupDist = Random.Range(3f, 5f);
            groupAngle = Random.Range(-50f, 50f);
            groupApproaching = true;
            nextGroupPath = 0f;
            nextGroupLook = now + Random.Range(1f, 3f);
            nextGroupToggle = now + Random.Range(8f, 20f);
            nextGroupCrouch = now + Random.Range(6f, 14f);
            nextFollowSpeak = now + Random.Range(3f, 7f);
            nextIdentityTry = now + 1.5f;
            followTimeVictim = p.OwnerClientId;
            followTime = 0f;
            followLimit = Random.Range(25f, 70f);
            exState = ExploreState.Pick;
            exViaDoor = false;
            hasLook = false;
            freeLookOn = false;
            agent.stoppingDistance = 0.4f;
            agent.isStopped = false;
            TryAdoptIdentityFor(p); // kimlik kurbanın kendisiyse değiştir
        }

        void UpdateGroup(float now)
        {
            var p = FindPlayer(groupTarget);
            if (p == null) { EndGroup(now, "oyuncu uzaklaştı"); return; }
            if (now >= groupUntil) { EndGroup(now, "süre doldu"); return; }
            float d = FlatDist(transform.position, p.transform.position);
            if (d > FollowRadius + 12f) { EndGroup(now, "çok uzak"); return; }
            for (int i = 0; i < All.Count; i++)
            {
                var m = All[i];
                if (m != null && m != this && m.activity == Activity.Group && m.groupTarget == groupTarget && m.groupStart < groupStart)
                { EndGroup(now, "başka taklitçi aynı grupta"); return; }
            }

            var prof = ProfileFor(p);
            if (targetClientId.Value == p.OwnerClientId && now >= nextIdentityTry)
            {
                nextIdentityTry = now + 1.5f;
                TryAdoptIdentityFor(p);
                prof = ProfileFor(p);
            }

            if (!groupApproaching && d > groupDist + 1.8f) groupApproaching = true;
            if (groupApproaching && d <= groupDist + 0.8f) { groupApproaching = false; agent.ResetPath(); }

            if (groupApproaching)
            {
                float ps = MovementProfiler.Get(p.OwnerClientId).currentSpeed;
                desiredSpeed = ps > SprintThreshold ? prof.sprintSpeed : Mathf.Clamp(prof.walkSpeed, 1.8f, 4.5f);
                if (crouching.Value) crouching.Value = false;
                freeLookOn = false;
                hasLook = d < 7f;
                if (hasLook) lookPoint = Head(p);
                if (now >= nextGroupPath)
                {
                    nextGroupPath = now + 0.5f;
                    Vector3 toM = transform.position - p.transform.position;
                    toM.y = 0f;
                    if (toM.sqrMagnitude < 0.01f) toM = -p.transform.forward;
                    Vector3 want = p.transform.position + Quaternion.Euler(0f, groupAngle, 0f) * toM.normalized * groupDist;
                    if (NavMesh.SamplePosition(want, out var hit, 3f, NavMesh.AllAreas)) agent.SetDestination(hit.position);
                }
                return;
            }

            // Yanında bir oyuncu gibi durur: küçük bakış değişimleri, çömelme, el feneri, konuşma
            if (agent.hasPath) agent.ResetPath();
            if (now >= nextGroupLook)
            {
                nextGroupLook = now + Random.Range(1.5f, 4f);
                if (Random.value < 0.6f) { hasLook = true; lookPoint = Head(p); freeLookOn = false; }
                else
                {
                    hasLook = false;
                    freeLookOn = true;
                    Vector3 to = p.transform.position - transform.position;
                    freeLookYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg + Random.Range(-70f, 70f);
                    freeLookPitch = Random.Range(-12f, 18f);
                }
            }
            if (now >= nextGroupCrouch)
            {
                if (crouching.Value) { crouching.Value = false; nextGroupCrouch = now + Random.Range(6f, 14f); }
                else if (Random.value < Mathf.Clamp(prof.crouchRatio + 0.15f, 0f, 0.6f))
                { crouching.Value = true; nextGroupCrouch = now + Random.Range(1.5f, 4f); }
                else nextGroupCrouch = now + Random.Range(6f, 14f);
            }
            if (now >= nextGroupToggle)
            {
                nextGroupToggle = now + Random.Range(8f, 20f);
                flashlightOn.Value = WantsFlashlight(transform.position, prof);
            }
            if (now >= nextFollowSpeak && now >= speakCooldownUntil)
            {
                nextFollowSpeak = now + (d <= 5f ? Random.Range(5f, 10f) : Random.Range(12f, 22f));
                bool ok = TrySpeak(p, FollowRadius, false, ClipPref.Call, "grup", now);
                speakCooldownUntil = now + (ok ? Random.Range(5f, 12f) : 2f);
            }
        }

        void EndGroup(float now, string why)
        {
            var p = FindPlayer(groupTarget);
            avoidPoint = p != null ? p.transform.position : transform.position;
            avoidUntil = now + 40f;
            hasLook = false;
            if (crouching.Value) crouching.Value = false;
            exState = ExploreState.Pick;
            nextPickAt = now;
            SetActivity(Activity.Explore, "grup bitti: " + why); // grup alanlarını da sıfırlar
        }

        // ---- Karanlık yer ve el feneri ----

        static bool IsDarkAt(Vector3 pos)
        {
            if (!FacilityGenerator.IsInteriorPoint(pos)) return true; // dışarısı gece
            var room = FacilityGenerator.RoomAt(pos);
            return room == null || room.dark;
        }

        static bool WantsFlashlight(Vector3 pos, MovementProfile prof)
            => IsDarkAt(pos) && Random.value < prof.flashlightRatio;

        // ---- İnsan gibi hareket: köşe/kapıda yavaşlama, kapı eşiğinde duraklama ----

        int NearestDoorIndex(Vector3 p, float radius)
        {
            var pts = FacilityGenerator.DoorPoints;
            int best = -1;
            float bestD = radius;
            for (int i = 0; i < pts.Count; i++)
            {
                if (Mathf.Abs(pts[i].y - p.y) > 2f) continue;
                float d = FlatDist(pts[i], p);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        void UpdateMovementFeel(float now, float dt)
        {
            if (atk != AttackState.None || !agent.isOnNavMesh) return;

            if (now < pauseUntil)
            {
                if (!agent.isStopped) agent.isStopped = true;
                return;
            }
            if (agent.isStopped) agent.isStopped = false;

            bool moving = agent.hasPath && !agent.pathPending && agent.velocity.sqrMagnitude > 0.04f;
            bool walkingAround = (activity == Activity.Explore && exState == ExploreState.Walk) || activity == Activity.Group;
            if (moving && walkingAround)
            {
                int di = NearestDoorIndex(transform.position, 1.3f);
                if (di >= 0 && di != lastPauseDoor)
                {
                    lastPauseDoor = di;
                    if (Random.value < 0.25f) { pauseUntil = now + Random.Range(0.4f, 1.2f); return; }
                }
                else if (di < 0 && lastPauseDoor >= 0 && NearestDoorIndex(transform.position, 3f) < 0) lastPauseDoor = -1;
            }

            float target = desiredSpeed;
            if (agent.hasPath && !agent.pathPending)
            {
                Vector3 vel = agent.velocity; vel.y = 0f;
                Vector3 toSteer = agent.steeringTarget - transform.position; toSteer.y = 0f;
                if (vel.sqrMagnitude > 0.25f && toSteer.sqrMagnitude > 0.01f && toSteer.magnitude < 1.8f)
                {
                    float ang = Vector3.Angle(vel, toSteer);
                    if (ang > 30f) target *= Mathf.Lerp(1f, 0.55f, Mathf.InverseLerp(30f, 90f, ang)); // köşede yavaşla
                }
                if (agent.remainingDistance < 1.5f) target = Mathf.Min(target, Mathf.Max(1.2f, desiredSpeed * 0.7f));
                if (NearestDoorIndex(transform.position, 1.6f) >= 0) target = Mathf.Min(target, desiredSpeed * 0.75f);
            }
            agent.speed = Mathf.MoveTowards(agent.speed, Mathf.Max(0.8f, target), 7f * dt);
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

                ulong id;
                if (IsSeen())
                {
                    // Görülürken kimlik değişmez; kurban kimlikten farklı olmalı
                    id = targetClientId.Value;
                    if (id == victim.OwnerClientId || !IsUsable(id)) { Reason("görülüyor (çağrı)"); continue; }
                }
                else
                {
                    if (!ResolveIdentity(victim, out id)) { Reason("aday yok (çağrı)"); continue; }
                    if (!ApplyIdentity(id)) { Reason("görülüyor (çağrı)"); continue; }
                }
                if (!FindLureSpot(victim, out var spotPos)) continue;

                SetActivity(Activity.Lure, "kurban " + victim.OwnerClientId + ", kimlik " + id);
                lureSpot = spotPos;
                lureVictim = victim.OwnerClientId;
                lureDeadline = now + 20f;
                lureSpoken = false;
                hasLook = false;
                crouching.Value = false;
                flashlightOn.Value = false;
                desiredSpeed = ProfileFor(victim).walkSpeed;
                agent.speed = desiredSpeed;
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
                if (TrySpeak(victim, LureRange, true, ClipPref.Call, "çağrı", now))
                {
                    lureSpoken = true;
                    speakCooldownUntil = now + Random.Range(5f, 12f);
                    lureDeadline = now + 4f;
                }
            }
        }

        void EndLure(float now)
        {
            exState = ExploreState.Pick;
            nextPickAt = now;
            hasLook = false;
            SetActivity(Activity.Explore, "çağrı bitti");
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
            if (!IsServer || !IsSpawned || atk != AttackState.None) return;
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

        bool IsUsable(ulong id) => ClipSharing.IsAllowed(id) && VoiceClipLibrary.Count(id) > 0;

        PlayerNetwork NearestOther(ulong exceptId, float radius)
        {
            PlayerNetwork best = null;
            float bestD = radius;
            for (int i = 0; i < tmpPlayers.Count; i++)
            {
                var p = tmpPlayers[i];
                if (p == null || p.OwnerClientId == exceptId) continue;
                float d = FlatDist(transform.position, p.transform.position);
                if (d <= bestD) { bestD = d; best = p; }
            }
            return best;
        }

        bool TrySpeak(PlayerNetwork victim, float radius, bool lockVictim, ClipPref pref, string ctx, float now)
        {
            if (revealed.Value || atk != AttackState.None) { Reason("açığa çıktı"); return false; }
            if (victim == null) { Reason("kurban yok"); return false; }
            if (now < speakingUntil) { Reason("zaten konuşuyor"); return false; }

            ulong id;
            if (IsSeen())
            {
                // Görülürken kimlik değişmez; kurbanı kimliğe göre seç
                id = targetClientId.Value;
                if (!IsUsable(id)) { Reason("görülüyor (kimlik uygun değil)"); return false; }
                if (victim.OwnerClientId == id)
                {
                    victim = lockVictim ? null : NearestOther(id, radius);
                    if (victim == null) { Reason("kurban yok"); return false; }
                }
            }
            else
            {
                if (!ResolveIdentity(victim, out id)) { Reason("aday yok"); return false; }
                if (!ApplyIdentity(id)) { Reason("görülüyor"); return false; }
            }

            var clips = VoiceClipLibrary.GetClips(id);
            if (clips.Count == 0) { Reason("konuşma klibi yok"); return false; }

            VoiceClip pick = null;
            // 1) tercih edilen tür + son 3'te yok, 2) diğer konuşma türü + son 3'te yok,
            // 3) tercih edilen tür, 4) herhangi konuşma klibi
            for (int pass = 0; pass < 4 && pick == null; pass++)
            {
                var pool = new List<VoiceClip>();
                for (int i = 0; i < clips.Count; i++)
                {
                    var c = clips[i];
                    if (!c.IsConversational) continue;
                    bool preferred = pref == ClipPref.Call ? c.isCall : c.isReply;
                    if ((pass == 0 || pass == 2) && !preferred) continue;
                    if (pass < 2 && IsRecent(id, c.id)) continue;
                    pool.Add(c);
                }
                if (pool.Count > 0) pick = pool[Random.Range(0, pool.Count)];
            }
            if (pick == null) { Reason("konuşma klibi yok"); return false; }

            recentClips[2] = recentClips[1];
            recentClips[1] = recentClips[0];
            recentClips[0] = ClipKey(id, pick.id);
            speakingUntil = now + pick.duration + 0.3f;
            lookOverrideId = victim.OwnerClientId;
            lookOverrideUntil = now + pick.duration + 0.5f;
            lastReason = "konuştu: " + ctx + ", klip " + pick.id + ", kurban " + victim.OwnerClientId;
            Debug.Log("[Taklitçi] Konuşuyor: kimlik " + id + ", klip " + pick.id + ", kurban " + victim.OwnerClientId + " (" + ctx + ", " + pick.duration.ToString("0.0") + " sn)");
            SpeakRpc(id, pick.id, BuildBlob(pick));
            return true;
        }

        static long ClipKey(ulong speaker, int clipId) => ((long)(speaker & 0xFFFFFF) << 32) | (uint)clipId;

        bool IsRecent(ulong speaker, int clipId)
        {
            long key = ClipKey(speaker, clipId);
            for (int i = 0; i < recentClips.Length; i++) if (recentClips[i] == key) return true;
            return false;
        }

        // ---------- Saldırı ----------

        bool IsIsolated(PlayerNetwork victim)
        {
            for (int i = 0; i < tmpPlayers.Count; i++)
            {
                var p = tmpPlayers[i];
                if (p == null || p == victim) continue;
                if (FlatDist(p.transform.position, victim.transform.position) <= IsolationRadius) return false;
            }
            return true;
        }

        // Her karar adımında: tetikleri say, biri tutarsa saldırıyı başlat
        bool CheckAttackTriggers(float now, PlayerNetwork nearest, float nearestD)
        {
            float dt = Mathf.Clamp(now - lastTriggerTick, 0f, 0.5f);
            lastTriggerTick = now;

            // 3) Takipte geçen toplam süre (aynı kurban)
            if (activity == Activity.Group && nearest != null && nearest.OwnerClientId == groupTarget)
                followTime += dt; // gruptaki toplam süre (başlangıç ve sınır StartGroup'ta)

            if (nearest == null || now < attackCooldownUntil || !agentReady)
            {
                isoTimer = 0f;
                behindTimer = 0f;
                return false;
            }
            ulong id = nearest.OwnerClientId;

            // 1) Yalnız kurbana yakın
            if (nearestD <= AttackIsolatedRange && IsIsolated(nearest))
            {
                if (isoVictim != id) { isoVictim = id; isoTimer = 0f; }
                isoTimer += dt;
            }
            else isoTimer = 0f;

            // 2) Kurbanın arkasında
            bool behind = false;
            if (nearestD <= AttackBehindRange)
            {
                Vector3 fwd = nearest.transform.forward; fwd.y = 0f;
                Vector3 to = transform.position - nearest.transform.position; to.y = 0f;
                behind = fwd.sqrMagnitude > 0.01f && to.sqrMagnitude > 0.0001f && Vector3.Angle(fwd, to) > AttackBehindAngle;
            }
            if (behind)
            {
                if (behindVictim != id) { behindVictim = id; behindTimer = 0f; }
                behindTimer += dt;
            }
            else behindTimer = 0f;

            string why = null;
            if (isoTimer >= AttackIsolatedTime) why = "yalnız";
            else if (behindTimer >= AttackBehindTime) why = "arkadan";
            else if (activity == Activity.Group && followTime >= followLimit) why = "sabırsızlandı";
            if (why == null) return false;

            StartAttack(nearest, why, now);
            return true;
        }

        void StartAttack(PlayerNetwork victim, string why, float now)
        {
            atk = AttackState.WindUp;
            atkVictim = victim.OwnerClientId;
            atkStart = now;
            atkReason = why;
            isoTimer = behindTimer = followTime = 0f;
            followTimeVictim = ulong.MaxValue;
            fleeHasDest = false;
            pendingSpeakAt = -1f;
            exState = ExploreState.Pick;
            nextPickAt = now;
            SetActivity(Activity.Explore, "saldırı"); // grup / bekleme / çağrı sona erer
            lookOverrideUntil = 0f;
            attackCooldownUntil = now + Random.Range(25f, 45f); // deneme sayılır

            crouching.Value = false;
            flashlightOn.Value = false;
            agent.ResetPath();
            agent.velocity = Vector3.zero;
            hasLook = true;
            lookPoint = Head(victim);
            revealed.Value = true;

            Debug.Log("[Taklitçi] Saldırı: kurban " + atkVictim + " (" + why + ")");
            SendRevealScream();
        }

        // Mevcut kimliğin rastgele bir klibini herkese yollar
        void SendRevealScream()
        {
            ulong id = targetClientId.Value;
            if (!IsUsable(id)) return;
            var clips = VoiceClipLibrary.GetClips(id);
            if (clips.Count == 0) return;
            var pick = clips[Random.Range(0, clips.Count)];
            speakingUntil = Time.time + pick.duration / RevealScreamRate + 0.3f;
            RevealRpc(id, pick.id, BuildBlob(pick));
        }

        void UpdateAttack(float now)
        {
            CollectPlayers();
            var victim = FindPlayer(atkVictim); // ölü/ayrılmış kurban null döner

            switch (atk)
            {
                case AttackState.WindUp:
                    if (victim == null) { BeginFlee(now, "kurban kayboldu"); return; }
                    hasLook = true;
                    lookPoint = Head(victim);
                    if (now - atkStart >= WindUpTime) BeginChase(now);
                    break;

                case AttackState.Chase:
                    UpdateChase(now, victim);
                    break;

                case AttackState.PostKill:
                    if (now - atkStart >= PostKillHold) BeginFlee(now, "öldürdü");
                    break;

                case AttackState.Flee:
                    UpdateFlee(now);
                    break;
            }
        }

        void BeginChase(float now)
        {
            atk = AttackState.Chase;
            chaseStart = now;
            lastSeenVictim = now;
            nextChasePath = 0f;
            nextSightCheck = 0f;
            baseStopping = agent.stoppingDistance;
            agent.isStopped = false;
            agent.speed = ChaseSpeed;
            agent.acceleration = ChaseAcceleration;
            agent.stoppingDistance = 0.2f;
        }

        void UpdateChase(float now, PlayerNetwork victim)
        {
            if (victim == null) { BeginFlee(now, "kurban kayboldu"); return; }

            Vector3 vp = victim.transform.position;
            float d = FlatDist(transform.position, vp);
            hasLook = true;
            lookPoint = Head(victim);

            if (d <= KillRange && Mathf.Abs(vp.y - transform.position.y) < 1.5f)
            {
                KillVictim(victim, now);
                return;
            }

            if (now >= nextChasePath)
            {
                nextChasePath = now + 0.15f;
                agent.SetDestination(NavMesh.SamplePosition(vp, out var hit, 2f, NavMesh.AllAreas) ? hit.position : vp);
            }

            if (now >= nextSightCheck)
            {
                nextSightCheck = now + 0.2f;
                if (HasLineOfSight(Head(victim), transform.position + Vector3.up * 1.2f)) lastSeenVictim = now;
            }

            if (d > ChaseGiveUpDistance) BeginFlee(now, "kurban kurtuldu (uzak)");
            else if (now - lastSeenVictim > ChaseLostSightTime) BeginFlee(now, "kurban gözden kayboldu");
            else if (now - chaseStart > ChaseMaxTime) BeginFlee(now, "kovalama süresi doldu");
        }

        void KillVictim(PlayerNetwork victim, float now)
        {
            Debug.Log("[Taklitçi] Öldürdü: " + victim.OwnerClientId);
            if (victim.Health != null) victim.Health.ServerKill("taklitçi");
            atk = AttackState.PostKill;
            atkStart = now;
            agent.ResetPath();
            agent.velocity = Vector3.zero;
        }

        void BeginFlee(float now, string why)
        {
            atk = AttackState.Flee;
            fleeStart = now;
            fleeAttemptStart = now;
            fleeRetries = 0;
            hasLook = false;
            agent.speed = ChaseSpeed;
            agent.acceleration = ChaseAcceleration;
            agent.stoppingDistance = 0.5f;
            Debug.Log("[Taklitçi] Saldırı sona erdi (" + why + "); kaçıyor.");
            StartFleeDestination();
        }

        void StartFleeDestination()
        {
            fleeHasDest = PickFleeSpot(out fleeDest);
            if (fleeHasDest) { agent.isStopped = false; agent.SetDestination(fleeDest); }
        }

        void UpdateFlee(float now)
        {
            CollectPlayers();
            bool arrived = !fleeHasDest ||
                FlatDist(transform.position, fleeDest) < 1.5f ||
                (!agent.pathPending && agent.hasPath && agent.remainingDistance <= agent.stoppingDistance + 0.3f);
            bool timeout = now - fleeAttemptStart > 25f;
            if (!arrived && !timeout) return;

            // Varınca hâlâ biri görüyorsa başka bir nokta dene (en çok 3 kez, toplam 40 sn)
            if (IsSeen() && fleeRetries < 3 && now - fleeStart < 40f)
            {
                fleeRetries++;
                fleeAttemptStart = now;
                StartFleeDestination();
                return;
            }
            EndAttack(now);
        }

        // Kimsenin görmediği, mümkünse oyunculardan >= 15 m uzak bir nokta
        bool PickFleeSpot(out Vector3 result)
        {
            result = default;
            int bestTier = -1;
            float bestScore = -1f;
            for (int i = 0; i < 24; i++)
            {
                float ang = Random.value * Mathf.PI * 2f;
                float r = Random.Range(FleeMinDistance, FleeMinDistance + 20f);
                var c = transform.position + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
                if (!NavMesh.SamplePosition(c, out var hit, 3f, NavMesh.AllAreas)) continue;
                if (FlatDist(hit.position, transform.position) < FleeMinDistance * 0.8f) continue;
                if (!NavMesh.CalculatePath(transform.position, hit.position, NavMesh.AllAreas, tmpPath)) continue;
                if (tmpPath.status != NavMeshPathStatus.PathComplete) continue;

                float minPlayer = 100f;
                bool seenByAny = false;
                for (int j = 0; j < tmpPlayers.Count; j++)
                {
                    var p = tmpPlayers[j];
                    if (p == null) continue;
                    minPlayer = Mathf.Min(minPlayer, FlatDist(p.transform.position, hit.position));
                    if (!seenByAny && HasLineOfSight(Head(p), hit.position + Vector3.up * 1.2f)) seenByAny = true;
                }
                int tier = !seenByAny && minPlayer >= FleeMinDistance ? 2 : (!seenByAny ? 1 : 0);
                if (tier > bestTier || (tier == bestTier && minPlayer > bestScore))
                {
                    bestTier = tier;
                    bestScore = minPlayer;
                    result = hit.position;
                }
            }
            return bestTier >= 0;
        }

        // Yeniden gizlen: gerçek biçim kapanır, normal davranış sürer
        void EndAttack(float now)
        {
            atk = AttackState.None;
            revealed.Value = false;
            attackCooldownUntil = now + Random.Range(25f, 45f);
            agent.acceleration = baseAcceleration;
            agent.stoppingDistance = baseStopping;
            agent.ResetPath();
            exState = ExploreState.Pick;
            nextPickAt = now + 0.5f;
            hasLook = false;
            freeLookOn = false;
            desiredSpeed = 3.5f;
            groupCooldownUntil = Mathf.Max(groupCooldownUntil, now + Random.Range(15f, 30f));
            SetActivity(Activity.Explore, "saldırı bitti");
            followVictim = ulong.MaxValue;
            lureCooldownUntil = Mathf.Max(lureCooldownUntil, now + Random.Range(10f, 20f));
            speakCooldownUntil = now + Random.Range(4f, 8f);
            identitySince = now - IdentityHold; // yeni kimlik seçilebilsin
            nextDecision = now + 0.5f;
            Debug.Log("[Taklitçi] Kaçtı / gizlendi");
        }

        string AttackDebug()
        {
            float now = Time.time;
            if (atk != AttackState.None)
                return atk + " | kurban " + atkVictim + " (" + atkReason + ") | " + (now - atkStart).ToString("0.0") + " sn";
            string cd = now < attackCooldownUntil ? "bekleme " + (attackCooldownUntil - now).ToString("0") + " sn" : "hazır";
            return "yok | " + cd + " | yalnız " + isoTimer.ToString("0.0") + "/" + AttackIsolatedTime.ToString("0.0")
                + " | arkadan " + behindTimer.ToString("0.0") + "/" + AttackBehindTime.ToString("0.0")
                + " | takip " + followTime.ToString("0") + "/" + followLimit.ToString("0") + " sn";
        }

        // ---------- Tanı ----------

        string ActivityDebug()
        {
            float now = Time.time;
            switch (activity)
            {
                case Activity.Explore:
                    return "Keşif/" + exState + " | hedef: " + (exDestInside ? "içeri" : "dışarı") + (exViaDoor ? " (kapıya)" : "")
                        + " | bölge planı: " + (zonePlanKind == 0 ? "yok" : (zonePlanKind == 1 ? "dışarı" : "içeri")
                        + (zonePlanAt < float.MaxValue ? " " + Mathf.Max(0f, zonePlanAt - now).ToString("0") + " sn" : " (vazgeçti)"));
                case Activity.Group:
                    return "Grup | oyuncu " + groupTarget + " | kalan " + Mathf.Max(0f, groupUntil - now).ToString("0") + " sn | mesafe " + groupDist.ToString("0.0") + " m";
                case Activity.Idle:
                    return "Bekleme | kalan " + Mathf.Max(0f, idleUntil - now).ToString("0") + " sn";
                default:
                    return "Çağrı | kurban " + lureVictim;
            }
        }

        public string DebugText()
        {
            return ActivityDebug() + " | kimlik: Oyuncu " + targetClientId.Value + " (renk " + colorIndex.Value + ") | ajan: " + (agentReady ? "hazır" : "YOK")
                + "\n    saldırı: " + AttackDebug()
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
            bool moving = agent.velocity.sqrMagnitude > 0.09f;
            if (look)
            {
                Vector3 eye = transform.position + Vector3.up * (shownHeight - EyeOffsetFromTop);
                Vector3 to = lp - eye;
                dir = to;
                float flat = new Vector2(to.x, to.z).magnitude;
                targetPitch = Mathf.Clamp(Mathf.Atan2(-to.y, Mathf.Max(0.01f, flat)) * Mathf.Rad2Deg, -45f, 45f);
            }
            else if (moving) dir = agent.velocity;                       // gövde yürüyüş yönüne döner
            else if (freeLookOn)
            {
                float yr = freeLookYaw * Mathf.Deg2Rad;
                dir = new Vector3(Mathf.Sin(yr), 0f, Mathf.Cos(yr));
                targetPitch = Mathf.Clamp(freeLookPitch, -45f, 45f);
            }
            else dir = Vector3.zero;

            dir.y = 0f;
            if (dir.sqrMagnitude > 0.04f)
            {
                var want = Quaternion.LookRotation(dir.normalized, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, want, TurnRate * Time.deltaTime);
            }
            serverPitch = Mathf.MoveTowards(serverPitch, targetPitch, 120f * Time.deltaTime);
            if (Mathf.Abs(serverPitch - pitch.Value) > 0.5f) pitch.Value = serverPitch;
        }
    }
}
