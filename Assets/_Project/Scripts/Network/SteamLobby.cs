using System;
using Netcode.Transports.Facepunch;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace MimicGame.Network
{
    /// <summary>Steam lobisi: oluşturma, davet, katılma. Sahneye eklenmesi gerekmez.</summary>
    public class SteamLobby : MonoBehaviour
    {
        public static SteamLobby Instance { get; private set; }

        public Lobby? CurrentLobby { get; private set; }
        public string Status { get; private set; } = "";

        NetworkManager subscribed;
        bool eventsHooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoCreate()
        {
            if (Instance != null) return;
            new GameObject("SteamLobby").AddComponent<SteamLobby>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void OnEnable()
        {
            if (eventsHooked) return;
            eventsHooked = true;
            SteamFriends.OnGameLobbyJoinRequested += OnJoinRequested;
            SteamMatchmaking.OnLobbyEntered += OnLobbyEntered;
        }

        void OnDisable()
        {
            if (!eventsHooked) return;
            eventsHooked = false;
            SteamFriends.OnGameLobbyJoinRequested -= OnJoinRequested;
            SteamMatchmaking.OnLobbyEntered -= OnLobbyEntered;
            if (subscribed != null) subscribed.OnClientStopped -= OnClientStopped;
            subscribed = null;
        }

        void Start()
        {
            // Oyun kapalıyken davetle açıldıysa
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out ulong id))
                {
                    JoinLobbyById(id);
                    break;
                }
            }
        }

        void Update()
        {
            // NetworkManager geç oluşabilir; lazy abone ol
            var nm = NetworkManager.Singleton;
            if (nm != null && subscribed != nm)
            {
                if (subscribed != null) subscribed.OnClientStopped -= OnClientStopped;
                subscribed = nm;
                nm.OnClientStopped += OnClientStopped;
            }
        }

        void OnClientStopped(bool wasHost) => Leave();

        public void SetStatus(string s) => Status = s;

        // ---------- Transport ----------

        public FacepunchTransport EnsureFacepunchTransport()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return null;
            var t = nm.GetComponent<FacepunchTransport>();
            if (t == null) t = nm.gameObject.AddComponent<FacepunchTransport>();
            return t;
        }

        public void UseFacepunch()
        {
            var nm = NetworkManager.Singleton;
            var t = EnsureFacepunchTransport();
            if (nm != null && t != null) nm.NetworkConfig.NetworkTransport = t;
        }

        public void UseUnityTransport()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            var utp = nm.GetComponent<UnityTransport>();
            if (utp != null) nm.NetworkConfig.NetworkTransport = utp;
        }

        // ---------- Host ----------

        public async void HostAsync()
        {
            var nm = NetworkManager.Singleton;
            if (!SteamBootstrap.Available || !SteamClient.IsValid)
            {
                Status = SteamBootstrap.Error;
                return;
            }
            if (nm == null) { Status = "NetworkManager bulunamadı."; return; }

            Status = "Lobi kuruluyor...";
            UseFacepunch();
            if (!nm.StartHost()) { Status = "Oyun kurulamadı."; return; }

            Lobby? lobby = null;
            try { lobby = await SteamMatchmaking.CreateLobbyAsync(4); }
            catch (Exception e) { Debug.LogWarning(e); }

            if (!lobby.HasValue)
            {
                Status = "Steam lobisi oluşturulamadı.";
                if (NetworkManager.Singleton != null) NetworkManager.Singleton.Shutdown();
                return;
            }

            var l = lobby.Value;
            l.SetFriendsOnly();
            l.SetJoinable(true);
            l.SetData("game", "MimicGame");
            l.SetData("host", SteamClient.Name);
            CurrentLobby = l;
            Status = "";
        }

        public void InviteFriends()
        {
            if (CurrentLobby.HasValue && SteamBootstrap.Available)
                SteamFriends.OpenGameInviteOverlay(CurrentLobby.Value.Id);
        }

        // ---------- Katılma ----------

        public async void JoinLobbyById(ulong id)
        {
            if (!SteamBootstrap.Available || !SteamClient.IsValid)
            {
                Status = SteamBootstrap.Error;
                return;
            }
            if (IsInGame()) { Status = "Zaten bir oyundasın."; return; }

            Status = "Lobiye katılınıyor...";
            Lobby? lobby = null;
            try { lobby = await SteamMatchmaking.JoinLobbyAsync(id); }
            catch (Exception e) { Debug.LogWarning(e); }

            // Başarılıysa OnLobbyEntered bağlantıyı başlatır
            if (!lobby.HasValue) Status = "Lobiye katılınamadı (ID yanlış veya lobi kapalı).";
        }

        async void OnJoinRequested(Lobby lobby, SteamId friend)
        {
            if (IsInGame()) { Status = "Zaten bir oyundasın."; return; }
            Status = "Lobiye katılınıyor...";
            RoomEnter result = RoomEnter.Error;
            try { result = await lobby.Join(); }
            catch (Exception e) { Debug.LogWarning(e); }
            if (result != RoomEnter.Success) Status = "Lobiye katılınamadı: " + result;
        }

        void OnLobbyEntered(Lobby lobby)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            CurrentLobby = lobby;
            if (nm.IsHost) return; // kendi lobimize girdik

            if (nm.IsListening) return; // zaten bağlanıyor/bağlı
            var t = EnsureFacepunchTransport();
            t.targetSteamId = lobby.Owner.Id;
            UseFacepunch();
            Status = "Bağlanıyor...";
            if (!nm.StartClient()) Status = "Bağlantı başlatılamadı.";
        }

        static bool IsInGame()
        {
            var nm = NetworkManager.Singleton;
            return nm != null && nm.IsListening;
        }

        public void Leave()
        {
            if (CurrentLobby.HasValue)
            {
                try { CurrentLobby.Value.Leave(); }
                catch (Exception e) { Debug.LogWarning(e); }
                CurrentLobby = null;
            }
        }
    }
}
