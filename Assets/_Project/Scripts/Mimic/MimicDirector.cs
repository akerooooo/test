using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.AI;
using MimicGame.Voice;

namespace MimicGame.Mimic
{
    /// <summary>Sunucuda taklitçileri doğurur. Hata ayıklama: F8 ekstra taklitçi, F9 etiketleri aç/kapat.</summary>
    public class MimicDirector : MonoBehaviour
    {
        public GameObject mimicPrefab;
        public Transform[] spawnPoints;
        public float firstSpawnDelay = 20f;
        public int mimicCount = 1;

        bool wasServer;
        float serverStart;
        bool spawnedInitial;
        bool showLabels; // varsayılan KAPALI (ayırt edilemezlik)

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
            }
            wasServer = server;
            if (!server) return;

            if (!spawnedInitial && Time.time - serverStart >= firstSpawnDelay)
            {
                spawnedInitial = true;
                for (int i = 0; i < mimicCount; i++) SpawnMimic();
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (nm.IsHost && Keyboard.current != null)
            {
                if (Keyboard.current.f8Key.wasPressedThisFrame) SpawnMimic();
                if (Keyboard.current.f9Key.wasPressedThisFrame) showLabels = !showLabels;
            }
#endif
        }

        public void SpawnMimic()
        {
            if (mimicPrefab == null) { Debug.LogWarning("[Taklitçi] Prefab atanmamış."); return; }
            Vector3 pos = transform.position;
            if (spawnPoints != null && spawnPoints.Length > 0)
            {
                var sp = spawnPoints[Random.Range(0, spawnPoints.Length)];
                if (sp != null) pos = sp.position;
            }
            if (NavMesh.SamplePosition(pos, out var hit, 4f, NavMesh.AllAreas)) pos = hit.position;

            var go = Instantiate(mimicPrefab, pos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            var no = go.GetComponent<NetworkObject>();
            if (no == null) { Destroy(go); return; }
            no.Spawn(true);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        void OnGUI()
        {
            if (!showLabels) return;
            var cam = Camera.main;
            if (cam == null) return;
            for (int i = 0; i < MimicAI.All.Count; i++)
            {
                var m = MimicAI.All[i];
                if (m == null) continue;
                Vector3 sp = cam.WorldToScreenPoint(m.transform.position + Vector3.up * 2.2f);
                if (sp.z <= 0f) continue;
                GUI.color = Color.red;
                GUI.Label(new Rect(sp.x - 100f, Screen.height - sp.y - 10f, 200f, 24f),
                    "TAKLİTÇİ → Oyuncu " + m.TargetClientId);
            }
            GUI.color = Color.white;
        }
#endif
    }
}
