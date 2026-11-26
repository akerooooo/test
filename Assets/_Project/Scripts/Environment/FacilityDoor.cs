using MimicGame.Interaction;
using MimicGame.Level;
using MimicGame.Network;
using MimicGame.Player;
using MimicGame.UI;
using UnityEngine;

namespace MimicGame.Environment
{
    /// <summary>
    /// Tesise giriş / tesisten çıkış kapısı. Sahip oyuncuyu kısa bir kararma ile ışınlar
    /// (sahip yetkili hareket; sunucu RPC'si gerekmez). Taklitçi yapay zekası için kapı önü çapaları sağlar.
    /// Bileşen, yerel +Z yönü kapının "ön" tarafına (kullanıcının durduğu yer) bakan kök nesnede bulunur.
    /// </summary>
    public class FacilityDoor : MonoBehaviour, IInteractable
    {
        /// <summary>Dış cephedeki kapı (içeri girer).</summary>
        public static FacilityDoor Exterior { get; private set; }
        /// <summary>Tesisin içindeki çıkış kapısı (dışarı çıkarır).</summary>
        public static FacilityDoor Interior { get; private set; }

        [Tooltip("Doğruysa tesisin içindeki çıkış kapısıdır (dışarı ışınlar).")]
        public bool isExit;
        public float fadeSeconds = 0.3f;
        [Tooltip("Kapı önü çapasının kapıdan uzaklığı (m).")]
        public float frontOffset = 1.6f;

        float nextUse;

        /// <summary>Kapının önünde, zeminde durma noktası (dünya).</summary>
        public Vector3 FrontPosition
        {
            get
            {
                Vector3 p = transform.position + FrontDirection * frontOffset;
                p.y = transform.position.y + 0.05f;
                return p;
            }
        }

        /// <summary>Kapıdan uzağa, kullanıcı tarafına bakan yatay yön (dünya).</summary>
        public Vector3 FrontDirection
        {
            get
            {
                Vector3 f = transform.forward;
                f.y = 0f;
                return f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
            }
        }

        void OnEnable()
        {
            if (isExit) Interior = this; else Exterior = this;
        }

        void OnDisable()
        {
            if (Interior == this) Interior = null;
            if (Exterior == this) Exterior = null;
        }

        public string GetPrompt(PlayerInteractor interactor)
        {
            if (!FacilityGenerator.Generated) return "Kapı kilitli";
            return isExit ? "Dışarı çık" : "İçeri gir";
        }

        public void Interact(PlayerInteractor interactor)
        {
            var gen = FacilityGenerator.Instance;
            if (gen == null || !FacilityGenerator.Generated || Time.unscaledTime < nextUse) return;
            var net = interactor != null ? interactor.GetComponent<PlayerNetwork>() : null;
            if (net == null || !net.IsSpawned || !net.IsOwner) return;

            nextUse = Time.unscaledTime + fadeSeconds + 0.5f;
            Vector3 dest = isExit ? gen.OutsideExitPoint : gen.ArrivalPoint;
            float yaw = isExit ? gen.OutsideExitYaw : gen.ArrivalYaw;
            ScreenFade.Run(fadeSeconds, () =>
            {
                if (net != null && net.IsSpawned) net.OwnerTeleport(dest, yaw);
            });
        }
    }
}
