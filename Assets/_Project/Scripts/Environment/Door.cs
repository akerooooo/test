using MimicGame.Interaction;
using MimicGame.Player;
using Unity.Netcode;
using UnityEngine;

namespace MimicGame.Environment
{
    /// <summary>
    /// Menteşe objesine eklenir. Kapı paneli bu objenin çocuğu olmalı.
    /// E ile açılır/kapanır; durumu sunucu tutar.
    /// </summary>
    public class Door : NetworkBehaviour, IInteractable
    {
        public float openAngle = -95f;
        public float speed = 5f;
        [Tooltip("Sunucunun kabul edeceği en uzak etkileşim mesafesi.")]
        public float maxUseDistance = 6f;

        readonly NetworkVariable<bool> isOpen = new NetworkVariable<bool>(false);

        public bool IsOpen => isOpen.Value;

        Quaternion closedRotation;

        void Awake() => closedRotation = transform.localRotation;

        void Update()
        {
            var target = closedRotation * Quaternion.Euler(0f, isOpen.Value ? openAngle : 0f, 0f);
            transform.localRotation = Quaternion.Slerp(transform.localRotation, target,
                1f - Mathf.Exp(-speed * Time.deltaTime));
        }

        public string GetPrompt(PlayerInteractor interactor) => isOpen.Value ? "Kapıyı kapat" : "Kapıyı aç";

        public void Interact(PlayerInteractor interactor)
        {
            if (IsSpawned) ToggleRpc();
        }

        [Rpc(SendTo.Server)]
        void ToggleRpc(RpcParams rpcParams = default)
        {
            var player = NetworkManager.SpawnManager.GetPlayerNetworkObject(rpcParams.Receive.SenderClientId);
            if (player != null && Vector3.Distance(player.transform.position, transform.position) > maxUseDistance) return;
            isOpen.Value = !isOpen.Value;
        }
    }
}
