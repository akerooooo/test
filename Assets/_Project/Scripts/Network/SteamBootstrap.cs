using Netcode.Transports.Facepunch;
using Steamworks;
using Unity.Netcode;
using UnityEngine;

namespace MimicGame.Network
{
    /// <summary>Steam'i başlatır, callback'leri çalıştırır, kapanışta kapatır. Sahneye eklenmesi gerekmez.</summary>
    public class SteamBootstrap : MonoBehaviour
    {
        public const uint AppId = 480; // Spacewar (test)

        public static SteamBootstrap Instance { get; private set; }
        public static bool Available { get; private set; }
        public static string Error { get; private set; } = "";
        public static string PlayerName => SteamClient.IsValid ? SteamClient.Name : "Oyuncu";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoCreate()
        {
            if (Instance != null) return;
            var go = new GameObject("SteamBootstrap");
            go.AddComponent<SteamBootstrap>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Çok oyunculu: pencere arka plandayken de oyun (ve ses) çalışmaya devam etsin
            Application.runInBackground = true;

            if (!SteamClient.IsValid)
            {
                try
                {
                    SteamClient.Init(AppId, false);
                }
                catch (System.Exception e)
                {
                    Available = false;
                    Error = "Steam başlatılamadı: Steam açık mı?";
                    Debug.LogWarning(e);
                    return;
                }
            }
            Available = SteamClient.IsValid;
            if (Available)
            {
                Error = "";
                // Steam sesi doğrudan çıkış hızında çözsün (ek yeniden örnekleme olmasın)
                try
                {
                    int rate = Mathf.Clamp(AudioSettings.outputSampleRate, 11025, 48000);
                    SteamUser.SampleRate = (uint)rate;
                    Debug.Log("[Ses] Steam ses çözme hızı: " + SteamUser.SampleRate + " Hz");
                }
                catch (System.Exception e) { Debug.LogWarning(e); }
            }
            else Error = "Steam başlatılamadı: Steam açık mı?";

            // Lobi yöneticisini de oluştur
            if (SteamLobby.Instance == null)
                new GameObject("SteamLobby").AddComponent<SteamLobby>();
        }

        void Update()
        {
            if (SteamClient.IsValid) SteamClient.RunCallbacks();
        }

        void OnApplicationQuit() => Shutdown();

        void OnDestroy()
        {
            if (Instance == this) { Shutdown(); Instance = null; }
        }

        static void Shutdown()
        {
            if (SteamClient.IsValid)
            {
                // Önce lobiden çık ve Steam soketlerini kapat; yoksa soket Steam'de açık kalır
                // ve sonraki oturumda "Invalid Socket" hatası alınır.
                try { if (SteamLobby.Instance != null) SteamLobby.Instance.Leave(); }
                catch (System.Exception e) { Debug.LogWarning(e); }

                var nm = NetworkManager.Singleton;
                if (nm != null)
                {
                    var t = nm.GetComponent<FacepunchTransport>();
                    if (t != null)
                    {
                        try { t.Shutdown(); }
                        catch (System.Exception e) { Debug.LogWarning(e); }
                    }
                }

                SteamClient.Shutdown();
            }
            Available = false;
        }
    }
}
