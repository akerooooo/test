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
    /// (sahip yetkili hareket; sunucu RPC'si gerekmez).
    /// </summary>
    public class FacilityDoor : MonoBehaviour, IInteractable
    {
        [Tooltip("Doğruysa tesisin içindeki çıkış kapısıdır (dışarı ışınlar).")]
        public bool isExit;
        public float fadeSeconds = 0.3f;

        float nextUse;

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
