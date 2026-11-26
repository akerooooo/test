using UnityEngine;

namespace MimicGame.Network
{
    /// <summary>Oyuncu doğmadan önce kullanılan sahne kamerası (işaretçi).</summary>
    public class MenuCamera : MonoBehaviour
    {
        public static MenuCamera Instance { get; private set; }

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        public static void SetActive(bool active)
        {
            if (Instance != null) Instance.gameObject.SetActive(active);
        }
    }
}
