using System.Text;
using MimicGame.Level;
using MimicGame.Network;
using MimicGame.Player;
using MimicGame.Voice;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

namespace MimicGame.Mimic
{
    /// <summary>Sunucuda taklitçileri doğurur. Hata ayıklama: F8 ekstra taklitçi (geliştirme), F9 tanı paneli (host).</summary>
    public class MimicDirector : MonoBehaviour
    {
        public GameObject mimicPrefab;
        public Transform[] spawnPoints;
        public float firstSpawnSeconds = 15f;
        [Tooltip("Aynı anda en fazla taklitçi sayısı.")]
        public int maxMimics = 6;

        const float MinSpawnDistance = 8f; // oyunculara asgari doğma uzaklığı
        const float MinMimicSpacing = 12f; // taklitçiler arası asgari doğma uzaklığı
        const float AllDeadResetDelay = 6f; // herkes ölünce tur sıfırlanana kadar
        const float KillCooldown = 60f;     // ölümden sonra yeni taklitçi doğmaz
        const float SpreadDistance = 20f;   // oyuncular bu kadar dağınıksa şans artar
        const float LongInsideSeconds = 180f;

        bool wasServer;
        float serverStart;
        float roundStart;
        bool spawnedInitial;
        float nextConsiderAt;
        float nextRetryAt;
        float lastKillTime = -999f;
        int lastAlive = -1;
        float insideSince = -1f;
        float allDeadSince = -1f;
        bool showDebug; // varsayılan KAPALI (ayırt edilemezlik)
        readonly StringBuilder sb = new StringBuilder(256);

        void Update()
        {
            var nm = NetworkManager.Singleton;
            bool server = nm != null && nm.IsServer && nm.IsListening;
            if (server && !wasServer)
            {
                serverStart = Time.time;
                roundStart = Time.time;
                spawnedInitial = false;
                lastAlive = -1;
                lastKillTime = -999f;
                insideSince = -1f;
            }
            else if (!server && wasServer)
            {
                SpeechTracker.Clear();
                ClipRecorder.Reset();
                showDebug = false;
                allDeadSince = -1f;
            }
            wasServer = server;
            if (!server) return;

            UpdateSituation();

            // Tesis üretilmeden taklitçi doğmaz (doğma noktaları oradan gelir)
            if (!spawnedInitial)
            {
                if (Time.time - serverStart >= firstSpawnSeconds && FacilityReady() && Time.time >= nextRetryAt)
                {
                    if (SpawnMimic())
                    {
                        spawnedInitial = true;
                        roundStart = Time.time;
                        nextConsiderAt = Time.time + Random.Range(60f, 120f);
                    }
                    else nextRetryAt = Time.time + 5f;
                }
            }
            else UpdateSpawning();

            UpdateRoundReset();

            if (nm.IsHost && Keyboard.current != null)
            {
                // Hata ayıklama: F7 yerel oyuncuyu öldürür, F6 herkesi canlandırır
                if (Keyboard.current.f7Key.wasPressedThisFrame && PlayerHealth.Local != null)
                    PlayerHealth.Local.ServerKill("hata ayıklama (F7)");
                if (Keyboard.current.f6Key.wasPressedThisFrame) ReviveAll();
                if (Keyboard.current.f9Key.wasPressedThisFrame) showDebug = !showDebug;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (Keyboard.current.f8Key.wasPressedThisFrame) SpawnMimic();
#endif
            }
        }

        // Herkes 6 sn ölü kalırsa: herkesi araç yanında canlandır, taklitçileri sıfırla (geçici tur döngüsü)
        static bool FacilityReady() => FacilityGenerator.Instance == null || FacilityGenerator.Generated;

        void UpdateRoundReset()
        {
            if (PlayerHealth.All.Count >= 1 && PlayerHealth.AliveCount == 0)
            {
                if (allDeadSince < 0f) allDeadSince = Time.time;
                else if (Time.time - allDeadSince >= AllDeadResetDelay)
                {
                    allDeadSince = -1f;
                    Debug.Log("[Tur] Herkes öldü; tur sıfırlanıyor.");
                    ReviveAll();
                    ServerResetRound();
                }
            }
            else allDeadSince = -1f;
        }

        void ReviveAll()
        {
            var list = new System.Collections.Generic.List<PlayerHealth>(PlayerHealth.All);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null || !list[i].IsSpawned) continue;
                var pos = new Vector3((i % 4) * 1.5f - 2.25f, 0.05f, -3f);
                list[i].ServerRevive(pos);
            }
        }

        /// <summary>Sunucu: mevcut taklitçileri kaldırır ve doğma zamanlayıcısını yeniden başlatır.</summary>
        public void ServerResetRound()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            var list = new System.Collections.Generic.List<MimicAI>(MimicAI.All);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null) continue;
                var no = list[i].NetworkObject;
                if (no != null && no.IsSpawned) no.Despawn(true);
            }
            serverStart = Time.time;
            roundStart = Time.time;
            spawnedInitial = false;
            lastKillTime = -999f;
            Debug.Log("[Taklitçi] Tur sıfırlandı; " + firstSpawnSeconds.ToString("0") + " sn sonra yeniden doğacak.");
        }

        public bool SpawnMimic()
        {
            if (mimicPrefab == null) { Debug.LogWarning("[Taklitçi] Prefab atanmamış."); return false; }
            if (!TryChooseSpawnPosition(out Vector3 pos)) { Debug.LogWarning("[Taklitçi] Doğma noktası yok."); return false; }
            if (NavMesh.SamplePosition(pos, out var hit, 4f, NavMesh.AllAreas)) pos = hit.position;
            else Debug.LogWarning("[Taklitçi] Doğma noktası NavMesh üzerinde değil: " + pos);

            var go = Instantiate(mimicPrefab, pos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            var no = go.GetComponent<NetworkObject>();
            if (no == null) { Destroy(go); return false; }
            no.Spawn(true);

            var ai = go.GetComponent<MimicAI>();
            Debug.Log("[Taklitçi] Doğdu: konum " + pos + ", kimlik " + (ai != null ? ai.TargetClientId.ToString() : "?"));
            Debug.Log("[Taklitçi] Yeni taklitçi doğdu (toplam " + (MimicAI.All.Count) + ", geçen süre " + ElapsedMinutes().ToString("0.0") + " dk)");
            return true;
        }

        // ---------- Doğma politikası: az başla, zamanla ve duruma göre artır ----------

        float ElapsedMinutes() => Mathf.Max(0f, (Time.time - roundStart) / 60f);

        void UpdateSituation()
        {
            int alive = PlayerHealth.AliveCount;
            if (lastAlive >= 0 && alive < lastAlive) lastKillTime = Time.time; // biri öldü
            lastAlive = alive;

            // Tüm canlı oyuncular uzun süredir içeride mi
            int inside = 0, total = 0;
            var all = PlayerNetwork.All;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == null || !all[i].IsSpawned || all[i].IsDead) continue;
                total++;
                if (all[i].IsInside) inside++;
            }
            if (total > 0 && inside == total) { if (insideSince < 0f) insideSince = Time.time; }
            else insideSince = -1f;
        }

        // Canlı oyuncuların ortalama ikili uzaklığı
        float AverageSpread()
        {
            var all = PlayerNetwork.All;
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == null || !all[i].IsSpawned || all[i].IsDead) continue;
                for (int j = i + 1; j < all.Count; j++)
                {
                    if (all[j] == null || !all[j].IsSpawned || all[j].IsDead) continue;
                    sum += Vector3.Distance(all[i].transform.position, all[j].transform.position);
                    n++;
                }
            }
            return n > 0 ? sum / n : 0f;
        }

        /// <summary>Bir sonraki değerlendirmede yeni taklitçi doğma olasılığı (0-1).</summary>
        public float SpawnChance()
        {
            if (Time.time - lastKillTime < KillCooldown) return 0f;
            float p = Mathf.Clamp01(ElapsedMinutes() / 10f);
            if (AverageSpread() > SpreadDistance) p += 0.2f;
            if (insideSince >= 0f && Time.time - insideSince > LongInsideSeconds) p += 0.15f;
            return Mathf.Clamp01(p);
        }

        void UpdateSpawning()
        {
            float now = Time.time;
            // Her zaman en az bir taklitçi bulunsun
            if (MimicAI.All.Count == 0)
            {
                if (now >= nextRetryAt)
                {
                    if (!SpawnMimic()) nextRetryAt = now + 5f;
                }
                return;
            }
            if (now < nextConsiderAt) return;
            nextConsiderAt = now + Random.Range(60f, 120f);
            if (MimicAI.All.Count >= maxMimics) return;
            float p = SpawnChance();
            if (Random.value < p) SpawnMimic();
            else Debug.Log("[Taklitçi] Yeni taklitçi doğmadı (şans %" + Mathf.RoundToInt(p * 100f) + ")");
        }

        // Oyuncuların ortalamasına en yakın, hepsinden >= MinSpawnDistance uzak ve kimsenin görüş hattında olmayan nokta.
        // Tesis üretildiyse noktalar oradan (iç mekân), yoksa sahnedeki doğma noktalarından gelir.
        bool TryChooseSpawnPosition(out Vector3 result)
        {
            result = transform.position;
            var candidates = new System.Collections.Generic.List<Vector3>();
            if (FacilityGenerator.Generated && FacilityGenerator.MimicSpawns.Count > 0)
                candidates.AddRange(FacilityGenerator.MimicSpawns);
            else if (spawnPoints != null)
                foreach (var sp in spawnPoints)
                    if (sp != null) candidates.Add(sp.position);
            if (candidates.Count == 0) return false;

            // Yalnızca doğma bölgesiyle (içeri/dışarı) aynı bölgedeki oyuncular hesaba katılır
            bool interior = FacilityGenerator.IsInteriorPoint(candidates[0]);
            var players = PlayerNetwork.All;
            Vector3 avg = Vector3.zero;
            int pc = 0;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i] == null || !players[i].IsSpawned || players[i].IsDead || players[i].IsInside != interior) continue;
                avg += players[i].transform.position;
                pc++;
            }
            if (pc > 0) avg /= pc;

            bool found = false;
            Vector3 best = candidates[0];
            float bestD = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                Vector3 p = candidates[i];
                bool ok = true;
                for (int j = 0; j < players.Count && ok; j++)
                {
                    var pl = players[j];
                    if (pl == null || !pl.IsSpawned || pl.IsDead || pl.IsInside != interior) continue;
                    if (Vector3.Distance(pl.transform.position, p) < MinSpawnDistance) ok = false;
                    else if (MimicAI.HasLineOfSight(MimicAI.Head(pl), p + Vector3.up * 1.2f)) ok = false;
                }
                for (int m = 0; m < MimicAI.All.Count && ok; m++)
                {
                    var other = MimicAI.All[m];
                    if (other != null && Vector3.Distance(other.transform.position, p) < MinMimicSpacing) ok = false;
                }
                if (!ok) continue;
                float d = pc > 0 ? Vector3.Distance(avg, p) : Random.value; // oyuncu yoksa rastgele
                if (d < bestD) { bestD = d; best = p; found = true; }
            }

            if (!found)
            {
                // Uygun nokta yok: taklitçilerden uzak rastgele biri
                best = candidates[Random.Range(0, candidates.Count)];
                for (int t = 0; t < 12; t++)
                {
                    var c = candidates[Random.Range(0, candidates.Count)];
                    bool near = false;
                    for (int m = 0; m < MimicAI.All.Count; m++)
                        if (MimicAI.All[m] != null && Vector3.Distance(MimicAI.All[m].transform.position, c) < MinMimicSpacing) near = true;
                    if (!near) { best = c; break; }
                }
                Debug.Log("[Taklitçi] Uzak ve görünmeyen doğma noktası yok; rastgele seçildi.");
            }
            result = best;
            return true;
        }

        void OnGUI()
        {
            if (!showDebug) return;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsHost) return;

            sb.Length = 0;
            sb.AppendLine("TAKLİTÇİ TANI (F9)");
            sb.Append("Taklitçi sayısı: ").Append(MimicAI.All.Count).Append(" / ").Append(maxMimics)
              .Append(" | geçen süre: ").Append(ElapsedMinutes().ToString("0.0")).Append(" dk")
              .Append(" | sonraki doğma şansı: %").Append(Mathf.RoundToInt(SpawnChance() * 100f))
              .Append(" (").Append(Mathf.Max(0f, nextConsiderAt - Time.time).ToString("0")).AppendLine(" sn sonra)");
            if (MimicAI.All.Count == 0) sb.AppendLine("Taklitçi yok (henüz doğmadı)");
            for (int i = 0; i < MimicAI.All.Count; i++)
            {
                var m = MimicAI.All[i];
                if (m == null) continue;
                sb.Append(i + 1).Append(") ").AppendLine(m.DebugText());
            }
            sb.AppendLine("Klip sayıları (çağrı/cevap/toplam):");
            var list = nm.ConnectedClientsList;
            for (int i = 0; i < list.Count; i++)
            {
                ulong id = list[i].ClientId;
                sb.Append("  Oyuncu ").Append(id).Append(": ").Append(VoiceClipLibrary.CountCalls(id)).Append('/')
                  .Append(VoiceClipLibrary.CountReplies(id)).Append('/').Append(VoiceClipLibrary.Count(id))
                  .Append(ClipSharing.IsAllowed(id) ? "" : " (taklit kapalı)").AppendLine();
            }
            sb.AppendLine("Davranış parçaları (oyuncu başına):");
            for (int i = 0; i < list.Count; i++)
            {
                ulong id = list[i].ClientId;
                sb.Append("  Oyuncu ").Append(id).Append(": ").AppendLine(BehaviorRecorder.DebugLine(id));
            }

            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            int lineCount = 1;
            string text = sb.ToString();
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lineCount++;
            var rect = new Rect(10f, 10f, 620f, 24f + 20f * lineCount);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, rect.height - 12f), text);

            var cam = Camera.main;
            if (cam == null) return;
            GUI.color = Color.red;
            for (int i = 0; i < MimicAI.All.Count; i++)
            {
                var m = MimicAI.All[i];
                if (m == null) continue;
                Vector3 sp = cam.WorldToScreenPoint(m.transform.position + Vector3.up * 2.2f);
                if (sp.z <= 0f) continue;
                GUI.Label(new Rect(sp.x - 100f, Screen.height - sp.y - 10f, 200f, 24f),
                    "TAKLİTÇİ [" + m.ActivityLabel + "] → Oyuncu " + m.TargetClientId);
            }
            GUI.color = Color.white;
        }
    }
}
