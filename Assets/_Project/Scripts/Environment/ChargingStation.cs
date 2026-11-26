using MimicGame.Interaction;
using MimicGame.Player;
using UnityEngine;

namespace MimicGame.Environment
{
    /// <summary>
    /// Araçtaki şarj istasyonu. E basılı tutulunca yerel oyuncunun fener pilini doldurur.
    /// Pil sahibine yerel olduğundan şarj da yereldir. Durum ışığı: kırmızı boşta, sarı şarjda, yeşil dolu.
    /// </summary>
    public class ChargingStation : MonoBehaviour, IHoldInteractable
    {
        public Renderer statusLight;
        public Light statusGlow;
        [Tooltip("Saniyede şarj edilen pil oranı (0-1).")]
        public float chargePerSecond = 0.2f;

        static readonly Color Idle = new Color(1f, 0.15f, 0.1f);
        static readonly Color Charging = new Color(1f, 0.65f, 0.1f);
        static readonly Color Full = new Color(0.2f, 1f, 0.3f);

        MaterialPropertyBlock block;
        int lastHoldFrame = -10;

        void Update()
        {
            bool charging = Time.frameCount - lastHoldFrame <= 1;
            var local = Flashlight.Local;
            Color c = charging ? Charging : (local != null && local.IsFull ? Full : Idle);
            Apply(c);
        }

        void Apply(Color c)
        {
            if (statusLight != null)
            {
                if (block == null) block = new MaterialPropertyBlock();
                statusLight.GetPropertyBlock(block);
                block.SetColor("_BaseColor", c);
                block.SetColor("_EmissionColor", c * 2.5f);
                statusLight.SetPropertyBlock(block);
            }
            if (statusGlow != null) statusGlow.color = c;
        }

        public string GetPrompt(PlayerInteractor interactor) => GetHoldPrompt(interactor);

        public string GetHoldPrompt(PlayerInteractor who)
        {
            var fl = who != null ? who.GetComponent<Flashlight>() : null;
            if (fl == null) return null;
            if (fl.IsFull) return "Fener dolu";
            return $"[E basılı tut] Feneri şarj et (%{fl.Battery01 * 100f:0})";
        }

        // Tek basış kullanılmaz; şarj HoldUpdate ile yapılır
        public void Interact(PlayerInteractor interactor) { }

        public bool HoldUpdate(PlayerInteractor who, float dt)
        {
            var fl = who != null ? who.GetComponent<Flashlight>() : null;
            if (fl == null || fl.IsFull) return false;
            fl.Charge(chargePerSecond * dt);
            who.HoldProgress01 = fl.Battery01;
            lastHoldFrame = Time.frameCount;
            return true;
        }
    }
}
