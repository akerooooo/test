using MimicGame.Game;
using MimicGame.Player;
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

        GUIStyle centerStyle, labelStyle, slotStyle;
        float styleScale = -1f;

        void Awake()
        {
            inventory = GetComponent<PlayerInventory>();
            motor = GetComponent<PlayerMotor>();
            interactor = GetComponent<PlayerInteractor>();
            flashlight = GetComponent<Flashlight>();
        }

        void OnGUI()
        {
            float s = Screen.height / 1080f;
            EnsureStyles(s);
            var white = Texture2D.whiteTexture;
            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

            // Nişangâh
            GUI.color = new Color(1f, 1f, 1f, 0.8f);
            GUI.DrawTexture(new Rect(center.x - 2f * s, center.y - 2f * s, 4f * s, 4f * s), white);
            GUI.color = Color.white;

            // Etkileşim yazısı
            if (interactor != null)
            {
                string prompt = interactor.Prompt;
                if (!string.IsNullOrEmpty(prompt))
                    Shadowed(new Rect(center.x - 400f * s, center.y + 24f * s, 800f * s, 40f * s), "[E] " + prompt, centerStyle);
            }

            // Envanter
            if (inventory != null)
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
                    GUI.color = i == inventory.Selected ? new Color(1f, 1f, 1f, 0.9f) : new Color(1f, 1f, 1f, 0.3f);
                    Border(r, 2f * s, white);
                    GUI.color = Color.white;
                    var item = inventory.Slots[i];
                    if (item != null) GUI.Label(r, item.displayName + "\n" + item.value + "$", slotStyle);
                }
                if (inventory.TotalWeight > 0f)
                    Shadowed(new Rect(x0, y - 30f * s, total, 26f * s), $"{inventory.TotalWeight:0.#} kg", centerStyle);
            }

            // Dayanıklılık
            if (motor != null && motor.Stamina01 < 0.999f)
                Bar(new Rect(30f * s, Screen.height - 60f * s, 220f * s, 10f * s), motor.Stamina01,
                    motor.IsExhausted ? new Color(0.9f, 0.3f, 0.2f) : new Color(0.9f, 0.9f, 0.9f), white);

            // Fener pili
            if (flashlight != null && flashlight.IsOn)
                Shadowed(new Rect(30f * s, Screen.height - 95f * s, 300f * s, 30f * s),
                    $"Fener: %{flashlight.Battery01 * 100f:0}", labelStyle);

            // Ganimet / kota
            var zone = LootZone.Main;
            if (zone != null)
                Shadowed(new Rect(30f * s, 25f * s, 800f * s, 34f * s),
                    $"Araçtaki ganimet: {zone.CollectedValue}$ / Kota: {zone.quota}$", labelStyle);

            if (Cursor.lockState != CursorLockMode.Locked)
                Shadowed(new Rect(0f, center.y - 60f * s, Screen.width, 40f * s), "Devam etmek için tıkla", centerStyle);
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
