using MimicGame.Game;
using MimicGame.Player;
using MimicGame.Voice;
using UnityEngine;

namespace MimicGame.UI
{
    /// <summary>
    /// Prototip arayüzü (OnGUI). Nişangâh, etkileşim yazısı, envanter,
    /// dayanıklılık, fener pili ve ganimet/kota. İleride gerçek UI ile değişecek.
    /// </summary>
    public class PlayerHUD : MonoBehaviour
    {
        PlayerInventory inventory;
        PlayerMotor motor;
        PlayerInteractor interactor;
        Flashlight flashlight;
        PlayerHealth health;

        GUIStyle centerStyle, labelStyle, slotStyle;
        float styleScale = -1f;

        // Mikrofon ikonu durumu
        const float MicIconShow = 1.2f, MicIconFade = 0.5f;
        bool micStateKnown, lastMuted;
        float micToggleTime = -100f;

        void Awake()
        {
            inventory = GetComponent<PlayerInventory>();
            motor = GetComponent<PlayerMotor>();
            interactor = GetComponent<PlayerInteractor>();
            flashlight = GetComponent<Flashlight>();
            health = GetComponent<PlayerHealth>();
        }

        void OnGUI()
        {
            float s = Screen.height / 1080f;
            EnsureStyles(s);
            var white = Texture2D.whiteTexture;
            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            bool dead = health != null && health.Dead;

            // Nişangâh (ölüyken gizli)
            if (!dead)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.8f);
                GUI.DrawTexture(new Rect(center.x - 2f * s, center.y - 2f * s, 4f * s, 4f * s), white);
                GUI.color = Color.white;
            }

            // Etkileşim yazısı
            if (!dead && interactor != null)
            {
                string prompt = interactor.Prompt;
                if (!string.IsNullOrEmpty(prompt))
                    Shadowed(new Rect(center.x - 400f * s, center.y + 24f * s, 800f * s, 40f * s), "[E] " + prompt, centerStyle);
            }

            // Envanter
            if (!dead && inventory != null)
            {
                float size = 80f * s, gap = 10f * s;
                float total = PlayerInventory.SlotCount * size + (PlayerInventory.SlotCount - 1) * gap;
                float x0 = (Screen.width - total) * 0.5f;
                float y = Screen.height - size - 30f * s;
                for (int i = 0; i < PlayerInventory.SlotCount; i++)
                {
                    var r = new Rect(x0 + i * (size + gap), y, size, size);
                    GUI.color = new Color(0f, 0f, 0f, 0.35f);
                    GUI.DrawTexture(r, white);
                    GUI.color = i == inventory.SelectedSlot ? new Color(1f, 1f, 1f, 0.9f) : new Color(1f, 1f, 1f, 0.3f);
                    Border(r, 2f * s, white);
                    GUI.color = Color.white;
                    var item = inventory.GetSlot(i);
                    if (item != null) GUI.Label(r, item.displayName + "\n" + item.value + "$", slotStyle);
                }
                if (inventory.TotalWeight > 0f)
                    Shadowed(new Rect(x0, y - 30f * s, total, 26f * s), $"{inventory.TotalWeight:0.#} kg", centerStyle);
            }

            // Dayanıklılık
            if (!dead && motor != null && motor.Stamina01 < 0.999f)
                Bar(new Rect(30f * s, Screen.height - 60f * s, 220f * s, 10f * s), motor.Stamina01,
                    motor.IsExhausted ? new Color(0.9f, 0.3f, 0.2f) : new Color(0.9f, 0.9f, 0.9f), white);

            // Fener pili
            if (!dead && flashlight != null && flashlight.IsOn)
                Shadowed(new Rect(30f * s, Screen.height - 95f * s, 300f * s, 30f * s),
                    $"Fener: %{flashlight.Battery01 * 100f:0}", labelStyle);

            if (dead) DrawSpectatorInfo(s, center);

            // Ganimet / kota
            var zone = LootZone.Main;
            if (zone != null)
                Shadowed(new Rect(30f * s, 25f * s, 800f * s, 34f * s),
                    $"Araçtaki ganimet: {zone.CollectedValue}$ / Kota: {zone.quota}$", labelStyle);

            // Mikrofon açıldı/kapandı ikonu: envanterin üstünde kısa süre görünür, sonra kaybolur
            DrawMicToggleIcon(s);

            if (Cursor.lockState != CursorLockMode.Locked)
                Shadowed(new Rect(0f, center.y - 60f * s, Screen.width, 40f * s), "Devam etmek için tıkla", centerStyle);
        }

        // Ölüyken: gözlem bilgisi; herkes ölmüşse ortada bilgi yazısı
        void DrawSpectatorInfo(float s, Vector2 center)
        {
            if (PlayerHealth.All.Count >= 1 && PlayerHealth.AliveCount == 0)
            {
                Shadowed(new Rect(0f, center.y - 20f * s, Screen.width, 40f * s), "Herkes öldü...", centerStyle);
                return;
            }
            var spec = health.spectator;
            string text = spec != null && spec.HasTarget
                ? "Öldün — izliyorsun: Oyuncu " + spec.WatchedClientId + " (Sol tık: değiştir)"
                : "Öldün";
            Shadowed(new Rect(0f, Screen.height - 70f * s, Screen.width, 40f * s), text, centerStyle);
        }

        void DrawMicToggleIcon(float s)
        {
            var voice = PlayerVoice.Local;
            if (voice == null) { micStateKnown = false; return; }

            bool muted = voice.IsMuted;
            if (!micStateKnown) { micStateKnown = true; lastMuted = muted; }
            else if (muted != lastMuted)
            {
                lastMuted = muted;
                micToggleTime = Time.unscaledTime;
            }

            float t = Time.unscaledTime - micToggleTime;
            if (t > MicIconShow + MicIconFade) return;
            float alpha = t <= MicIconShow ? 1f : 1f - (t - MicIconShow) / MicIconFade;

            // Envanterin (ve ağırlık yazısının) üstünde, ortada
            float iconSize = 56f * s;
            float invTop = Screen.height - 80f * s - 30f * s;
            var r = new Rect((Screen.width - iconSize) * 0.5f, invTop - 36f * s - iconSize, iconSize, iconSize);

            var tex = muted ? MicIcon.Off : MicIcon.On;
            var color = muted ? new Color(0.95f, 0.25f, 0.2f) : new Color(1f, 1f, 1f);

            // Gölge + ikon
            GUI.color = new Color(0f, 0f, 0f, 0.6f * alpha);
            GUI.DrawTexture(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), tex);
            GUI.color = new Color(color.r, color.g, color.b, alpha);
            GUI.DrawTexture(r, tex);
            GUI.color = Color.white;
        }

        void EnsureStyles(float s)
        {
            if (Mathf.Approximately(s, styleScale) && centerStyle != null) return;
            styleScale = s;
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(24f * s) };
            labelStyle.normal.textColor = Color.white;
            centerStyle = new GUIStyle(labelStyle) { alignment = TextAnchor.MiddleCenter };
            slotStyle = new GUIStyle(centerStyle) { fontSize = Mathf.RoundToInt(15f * s), wordWrap = true };
        }

        static void Shadowed(Rect r, string text, GUIStyle style)
        {
            var c = style.normal.textColor;
            style.normal.textColor = Color.black;
            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, style);
            style.normal.textColor = c;
            GUI.Label(r, text, style);
        }

        static void Border(Rect r, float t, Texture2D tex)
        {
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), tex);
            GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), tex);
            GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), tex);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), tex);
        }

        static void Bar(Rect r, float v, Color color, Texture2D tex)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(r, tex);
            GUI.color = color;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(v), r.height), tex);
            GUI.color = Color.white;
        }
    }
}
