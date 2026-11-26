using System.Collections.Generic;
using MimicGame.Mimic;
using Steamworks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace MimicGame.Network
{
    /// <summary>Bağlanmadan önce Host/Katıl menüsü; bağlıyken Esc ile çıkan "Bağlantıyı Kes" düğmesi.</summary>
    public class NetworkMenu : MonoBehaviour
    {
        string ip = "127.0.0.1";
        string portText = "7777";
        string status = "";
        string lobbyIdText = "";
        int tab = -1; // 0 = Steam, 1 = Yerel (IP)

        // Oyun içi arkadaş paneli (overlay yokken)
        static bool friendsPanelOpen;
        List<Friend> friends = new List<Friend>();
        Vector2 friendsScroll;

        GUIStyle labelStyle, centerStyle, fieldStyle, buttonStyle, toggleStyle;
        float styleScale = -1f;
        NetworkManager subscribed;

        void Start() => Subscribe();

        void OnDisable()
        {
            if (subscribed != null) subscribed.OnClientDisconnectCallback -= OnDisconnected;
            subscribed = null;
        }

        void Subscribe()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || subscribed == nm) return;
            subscribed = nm;
            nm.OnClientDisconnectCallback += OnDisconnected;
        }

        void OnDisconnected(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            // Host için yalnızca kendi bağlantısı önemli
            if (nm.IsServer && clientId != nm.LocalClientId) return;
            string reason = nm.DisconnectReason;
            friendsPanelOpen = false;
            status = string.IsNullOrEmpty(reason) ? "Bağlantı kesildi veya kurulamadı." : "Bağlantı kesildi: " + reason;
            if (SteamLobby.Instance != null) { SteamLobby.Instance.SetStatus(status); SteamLobby.Instance.Leave(); }
            MenuCamera.SetActive(true);
        }

        static bool Connected(NetworkManager nm) => nm.IsListening && (nm.IsServer || nm.IsConnectedClient);

        void Update()
        {
            Subscribe();
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            if (Connected(nm) && SteamLobby.Instance != null && !string.IsNullOrEmpty(SteamLobby.Instance.Status))
                SteamLobby.Instance.SetStatus("");
            // Menü açıkken imleç serbest
            if (!Connected(nm))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        void OnGUI()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
            float s = Screen.height / 1080f;
            EnsureStyles(s);

            if (Connected(nm))
            {
                if (Cursor.lockState != CursorLockMode.Locked)
                {
                    if (GUI.Button(DisconnectRect(), "Bağlantıyı Kes", buttonStyle))
                    {
                        nm.Shutdown();
                        if (SteamLobby.Instance != null) SteamLobby.Instance.Leave();
                        friendsPanelOpen = false;
                        status = "";
                        MenuCamera.SetActive(true);
                        Cursor.lockState = CursorLockMode.None;
                        Cursor.visible = true;
                    }
                    if (HasHostLobby())
                    {
                        var lobby = SteamLobby.Instance;
                        if (GUI.Button(InviteRect(), "Arkadaş Davet Et", buttonStyle))
                        {
                            // Overlay yoksa oyun içi listeyi aç/kapat
                            if (!lobby.InviteFriends())
                            {
                                friendsPanelOpen = !friendsPanelOpen;
                                if (friendsPanelOpen) friends = lobby.GetOnlineFriends();
                            }
                        }
                        string id = ((ulong)lobby.CurrentLobby.Value.Id).ToString();
                        var idRect = IdRect();
                        GUI.Label(idRect, "Lobi ID: " + id, new GUIStyle(labelStyle) { alignment = TextAnchor.MiddleRight, fontSize = Mathf.RoundToInt(16f * s) });
                        if (GUI.Button(CopyRect(), "Kopyala", buttonStyle)) GUIUtility.systemCopyBuffer = id;
                        if (friendsPanelOpen) DrawFriendsPanel(lobby, s);
                    }
                    else friendsPanelOpen = false;
                }
                return;
            }
            friendsPanelOpen = false;

            bool connecting = nm.IsListening;
            if (tab < 0) tab = SteamBootstrap.Available ? 0 : 1;
            var white = Texture2D.whiteTexture;
            float ph = (tab == 0 ? 656f : 616f) * s;
            var panel = new Rect((Screen.width - 520f * s) * 0.5f, (Screen.height - ph) * 0.5f, 520f * s, ph);
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(panel, white);
            GUI.color = Color.white;

            float x = panel.x + 40f * s, w = panel.width - 80f * s, y = panel.y + 20f * s, h = 44f * s, gap = 14f * s;
            GUI.Label(new Rect(x, y, w, 70f * s), "MimicGame", new GUIStyle(centerStyle) { fontSize = Mathf.RoundToInt(48f * s) });
            y += 80f * s;

            if (connecting)
            {
                GUI.Label(new Rect(x, y, w, h), "Bağlanıyor...", centerStyle);
                y += h + gap;
                if (GUI.Button(new Rect(x, y, w, h), "İptal", buttonStyle))
                {
                    nm.Shutdown();
                    if (SteamLobby.Instance != null) SteamLobby.Instance.Leave();
                    status = "";
                }
                return;
            }

            // Sekmeler
            float tw = (w - gap) * 0.5f;
            if (GUI.Toggle(new Rect(x, y, tw, h), tab == 0, "Steam", buttonStyle)) tab = 0;
            if (GUI.Toggle(new Rect(x + tw + gap, y, tw, h), tab == 1, "Yerel (IP)", buttonStyle)) tab = 1;
            y += h + gap * 2f;

            var small = new GUIStyle(centerStyle) { wordWrap = true, fontSize = Mathf.RoundToInt(18f * s) };
            if (tab == 0) DrawSteamTab(x, y, w, h, gap, s, small);
            else DrawLocalTab(nm, x, y, w, h, gap, small);

            // Gizlilik: sesimin taklit edilmesine izin ver
            bool allow = GUI.Toggle(new Rect(x, panel.yMax - 50f * s, w, 36f * s), ClipSharing.AllowMimicry,
                "Sesimin taklit edilmesine izin ver", toggleStyle);
            if (allow != ClipSharing.AllowMimicry) ClipSharing.AllowMimicry = allow;
        }

        void DrawFriendsPanel(SteamLobby lobby, float s)
        {
            var panel = FriendsRect();
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = Color.white;

            float pad = 14f * s, bh = 40f * s, rowH = 44f * s;
            float x = panel.x + pad, w = panel.width - pad * 2f, y = panel.y + pad;
            GUI.Label(new Rect(x, y, w, 36f * s), "Arkadaşlar", centerStyle);
            y += 40f * s;

            float bw = (w - 10f * s) * 0.5f;
            if (GUI.Button(new Rect(x, y, bw, bh), "Yenile", buttonStyle)) friends = lobby.GetOnlineFriends();
            if (GUI.Button(new Rect(x + bw + 10f * s, y, bw, bh), "Kapat", buttonStyle)) { friendsPanelOpen = false; return; }
            y += bh + 10f * s;

            float statusH = 50f * s;
            var view = new Rect(x, y, w, panel.yMax - pad - statusH - y);
            if (friends == null || friends.Count == 0)
            {
                GUI.Label(view, "Çevrimiçi arkadaş yok.", centerStyle);
            }
            else
            {
                var content = new Rect(0f, 0f, w - 20f * s, friends.Count * rowH);
                friendsScroll = GUI.BeginScrollView(view, friendsScroll, content);
                var nameStyle = new GUIStyle(labelStyle) { alignment = TextAnchor.MiddleLeft, fontSize = Mathf.RoundToInt(18f * s), clipping = TextClipping.Clip };
                float btnW = 90f * s;
                for (int i = 0; i < friends.Count; i++)
                {
                    var f = friends[i];
                    string label = f.IsPlayingThisGame ? f.Name + " (oyunda)" : f.Name;
                    GUI.Label(new Rect(0f, i * rowH, content.width - btnW - 6f * s, rowH), label, nameStyle);
                    if (GUI.Button(new Rect(content.width - btnW, i * rowH + 3f * s, btnW, rowH - 6f * s), "Davet", buttonStyle))
                        lobby.InviteFriend(f);
                }
                GUI.EndScrollView();
            }

            var small = new GUIStyle(labelStyle) { wordWrap = true, alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(16f * s) };
            GUI.Label(new Rect(x, panel.yMax - pad - statusH, w, statusH), lobby.Status, small);
        }

        void DrawSteamTab(float x, float y, float w, float h, float gap, float s, GUIStyle small)
        {
            var lobby = SteamLobby.Instance;
            if (!SteamBootstrap.Available || lobby == null)
            {
                GUI.Label(new Rect(x, y, w, 80f * s), SteamBootstrap.Error, small);
                return;
            }

            GUI.Label(new Rect(x, y, w, h), "Steam: " + SteamBootstrap.PlayerName, centerStyle);
            y += h + gap;
            if (GUI.Button(new Rect(x, y, w, h), "Oyun Kur (Steam)", buttonStyle)) lobby.HostAsync();
            y += h + gap * 2f;

            GUI.Label(new Rect(x, y, w, h * 0.7f), "Lobi ID", labelStyle);
            y += h * 0.7f;
            lobbyIdText = GUI.TextField(new Rect(x, y, w, h), lobbyIdText, 24, fieldStyle);
            y += h + gap;
            if (GUI.Button(new Rect(x, y, w, h), "Lobi ID ile Katıl", buttonStyle))
            {
                if (ulong.TryParse(lobbyIdText.Trim(), out ulong id)) lobby.JoinLobbyById(id);
                else lobby.SetStatus("Geçersiz lobi ID.");
            }
            y += h + gap;

            GUI.Label(new Rect(x, y, w, 50f * s), "Arkadaşların Steam'den davetini kabul ederek de katılabilir.", small);
            y += 54f * s;
            if (!string.IsNullOrEmpty(lobby.Status))
                GUI.Label(new Rect(x, y, w, 60f * s), lobby.Status, small);
        }

        void DrawLocalTab(NetworkManager nm, float x, float y, float w, float h, float gap, GUIStyle small)
        {
            GUI.Label(new Rect(x, y, w, h * 0.7f), "IP adresi", labelStyle);
            y += h * 0.7f;
            ip = GUI.TextField(new Rect(x, y, w, h), ip, 64, fieldStyle);
            y += h + gap;
            GUI.Label(new Rect(x, y, w, h * 0.7f), "Port", labelStyle);
            y += h * 0.7f;
            portText = GUI.TextField(new Rect(x, y, w, h), portText, 5, fieldStyle);
            y += h + gap * 2f;

            if (GUI.Button(new Rect(x, y, w, h), "Host (Oyunu Kur)", buttonStyle)) Begin(nm, true);
            y += h + gap;
            if (GUI.Button(new Rect(x, y, w, h), "Katıl", buttonStyle)) Begin(nm, false);
            y += h + gap;

            if (!string.IsNullOrEmpty(status))
                GUI.Label(new Rect(x, y, w, h * 1.5f), status, small);
        }

        void Begin(NetworkManager nm, bool host)
        {
            if (SteamLobby.Instance != null) SteamLobby.Instance.UseUnityTransport();
            var utp = nm.GetComponent<UnityTransport>();
            if (utp == null) utp = nm.NetworkConfig.NetworkTransport as UnityTransport;
            if (utp == null) { status = "UnityTransport bulunamadı."; return; }
            if (!ushort.TryParse(portText, out ushort port)) { status = "Geçersiz port."; return; }

            string address = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip.Trim();
            status = "";
            bool ok;
            if (host)
            {
                utp.SetConnectionData(address, port, "0.0.0.0");
                ok = nm.StartHost();
            }
            else
            {
                utp.SetConnectionData(address, port);
                ok = nm.StartClient();
            }
            if (!ok) status = host ? "Oyun kurulamadı." : "Bağlantı başlatılamadı.";
        }

        // PlayerLook, düğmeye tıklarken imleci geri kilitlemesin diye
        public static Rect DisconnectRect()
        {
            float s = Screen.height / 1080f;
            float w = 240f * s, h = 44f * s;
            return new Rect(Screen.width - w - 20f * s, 20f * s, w, h);
        }

        /// <param name="mousePos">Ekran koordinatı (sol-alt orijin).</param>
        public static bool PointerOverButton(Vector2 mousePos)
        {
            var p = new Vector2(mousePos.x, Screen.height - mousePos.y);
            if (DisconnectRect().Contains(p)) return true;
            if (HasHostLobby() && (InviteRect().Contains(p) || CopyRect().Contains(p))) return true;
            if (friendsPanelOpen && HasHostLobby() && FriendsRect().Contains(p)) return true;
            return false;
        }

        static Rect RowRect(int row)
        {
            float s = Screen.height / 1080f;
            float w = 240f * s, h = 44f * s;
            return new Rect(Screen.width - w - 20f * s, 20f * s + row * (h + 8f * s), w, h);
        }

        // Arkadaş paneli: düğme sütununun soluna (GUI koordinatı, y aşağı)
        public static Rect FriendsRect()
        {
            float s = Screen.height / 1080f;
            float w = 420f * s, h = 560f * s;
            return new Rect(Screen.width - 240f * s - 40f * s - w, 20f * s, w, h);
        }

        public static Rect InviteRect() => RowRect(1);
        static Rect IdRect() => RowRect(2);
        public static Rect CopyRect() => RowRect(3);

        // Host mu ve açık bir Steam lobisi var mı
        static bool HasHostLobby()
        {
            var nm = NetworkManager.Singleton;
            var l = SteamLobby.Instance;
            return nm != null && nm.IsHost && l != null && l.CurrentLobby.HasValue;
        }

        void EnsureStyles(float s)
        {
            if (Mathf.Approximately(s, styleScale) && labelStyle != null) return;
            styleScale = s;
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(20f * s) };
            labelStyle.normal.textColor = Color.white;
            centerStyle = new GUIStyle(labelStyle) { alignment = TextAnchor.MiddleCenter };
            fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = Mathf.RoundToInt(22f * s), alignment = TextAnchor.MiddleLeft };
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(22f * s) };
            toggleStyle = new GUIStyle(GUI.skin.toggle) { fontSize = Mathf.RoundToInt(18f * s) };
            toggleStyle.normal.textColor = Color.white;
            toggleStyle.onNormal.textColor = Color.white;
            toggleStyle.hover.textColor = Color.white;
            toggleStyle.onHover.textColor = Color.white;
        }
    }
}
