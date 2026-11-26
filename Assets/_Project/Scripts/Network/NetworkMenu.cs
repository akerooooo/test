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

        GUIStyle labelStyle, centerStyle, fieldStyle, buttonStyle;
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
            status = string.IsNullOrEmpty(reason) ? "Bağlantı kesildi veya kurulamadı." : "Bağlantı kesildi: " + reason;
            MenuCamera.SetActive(true);
        }

        static bool Connected(NetworkManager nm) => nm.IsListening && (nm.IsServer || nm.IsConnectedClient);

        void Update()
        {
            Subscribe();
            var nm = NetworkManager.Singleton;
            if (nm == null) return;
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
                if (Cursor.lockState != CursorLockMode.Locked &&
                    GUI.Button(DisconnectRect(), "Bağlantıyı Kes", buttonStyle))
                {
                    nm.Shutdown();
                    status = "";
                    MenuCamera.SetActive(true);
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                return;
            }

            bool connecting = nm.IsListening;
            var white = Texture2D.whiteTexture;
            var panel = new Rect((Screen.width - 520f * s) * 0.5f, (Screen.height - 520f * s) * 0.5f, 520f * s, 520f * s);
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
                    status = "";
                }
                return;
            }

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
                GUI.Label(new Rect(x, y, w, 60f * s), status, new GUIStyle(centerStyle) { wordWrap = true, fontSize = Mathf.RoundToInt(18f * s) });
        }

        void Begin(NetworkManager nm, bool host)
        {
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
            return DisconnectRect().Contains(p);
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
        }
    }
}
