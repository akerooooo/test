using System.Text;
using MimicGame.Network;
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
        public int mimicCount = 1;

        const float MinSpawnDistance = 12f;

        bool wasServer;
        float serverStart;
        bool spawnedInitial;
        bool showDebug; // varsayılan KAPALI (ayırt edilemezlik)
        readonly StringBuilder sb = new StringBuilder(256);

        void Update()
        {
            var nm = NetworkManager.Singleton;
            bool server = nm != null && nm.IsServer && nm.IsListening;
            if (server && !wasServer)
            {
                serverStart = Time.time;
                spawnedInitial = false;
            }
            else if (!server && wasServer)
            {
                SpeechTracker.Clear();
                ClipRecorder.Reset();
                showDebug = false;
            }
            wasServer = server;
            if (!server) return;

            if (!spawnedInitial && Time.time - serverStart >= firstSpawnSeconds)
            {
                spawnedInitial = true;
                for (int i = 0; i < mimicCount; i++) SpawnMimic();
            }

            if (nm.IsHost && Keyboard.current != null)
            {
                if (Keyboard.current.f9Key.wasPressedThisFrame) showDebug = !showDebug;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (Keyboard.current.f8Key.wasPressedThisFrame) SpawnMimic();
#endif
            }
        }

        public void SpawnMimic()
        {
            if (mimicPrefab == null) { Debug.LogWarning("[Taklitçi] Prefab atanmamış."); return; }
            Vector3 pos = ChooseSpawnPosition();
            if (NavMesh.SamplePosition(pos, out var hit, 4f, NavMesh.AllAreas)) pos = hit.position;
            else Debug.LogWarning("[Taklitçi] Doğma noktası NavMesh üzerinde değil: " + pos);

            var go = Instantiate(mimicPrefab, pos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            var no = go.GetComponent<NetworkObject>();
            if (no == null) { Destroy(go); return; }
            no.Spawn(true);

            var ai = go.GetComponent<MimicAI>();
            Debug.Log("[Taklitçi] Doğdu: konum " + pos + ", kimlik " + (ai != null ? ai.TargetClientId.ToString() : "?"));
        }

        // Oyuncuların ortalamasına en yakın, hepsinden >= 12 m uzak ve kimsenin görüş hattında olmayan nokta
        Vector3 ChooseSpawnPosition()
        {
            if (spawnPoints == null || spawnPoints.Length == 0) return transform.position;

            var players = PlayerNetwork.All;
            Vector3 avg = Vector3.zero;
            int pc = 0;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i] == null || !players[i].IsSpawned) continue;
                avg += players[i].transform.position;
                pc++;
            }
            if (pc > 0) avg /= pc;

            Transform best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < spawnPoints.Length; i++)
            {
                var sp = spawnPoints[i];
                if (sp == null) continue;
                Vector3 p = sp.position;
                bool ok = true;
                for (int j = 0; j < players.Count && ok; j++)
                {
                    var pl = players[j];
                    if (pl == null || !pl.IsSpawned) continue;
                    if (Vector3.Distance(pl.transform.position, p) < MinSpawnDistance) ok = false;
                    else if (MimicAI.HasLineOfSight(MimicAI.Head(pl), p + Vector3.up * 1.2f)) ok = false;
                }
                if (!ok) continue;
                float d = pc > 0 ? Vector3.Distance(avg, p) : 0f;
                if (d < bestD) { bestD = d; best = sp; }
            }

            if (best == null)
            {
                // Uygun nokta yok: herhangi biri
                for (int tries = 0; tries < 8 && best == null; tries++)
                    best = spawnPoints[Random.Range(0, spawnPoints.Length)];
                if (best == null) return transform.position;
                Debug.LogWarning("[Taklitçi] Uygun doğma noktası yok (uzaklık/görüş); rastgele seçildi.");
            }
            return best.position;
        }

        void OnGUI()
        {
            if (!showDebug) return;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsHost) return;

            sb.Length = 0;
            sb.AppendLine("TAKLİTÇİ TANI (F9)");
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

            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            var rect = new Rect(10f, 10f, 520f, 40f + 20f * (4 + MimicAI.All.Count * 4 + list.Count));
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, rect.height - 12f), sb.ToString());

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
                    "TAKLİTÇİ → Oyuncu " + m.TargetClientId);
            }
            GUI.color = Color.white;
        }
    }
}
