using System.Collections.Generic;
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

        [Tooltip("Geçiş noktası (açıklığın ortası); boşsa menteşenin kendisi.")]
        public Transform passPoint;

        /// <summary>Sahnedeki doğmuş kapılar (taklitçi yapay zekası için).</summary>
        public static readonly List<Door> All = new List<Door>();

        /// <summary>Sunucu: bir oyuncu kapıyı açtı/kapattı (clientId, şimdi açık mı).</summary>
        public static event System.Action<ulong, bool> PlayerToggled;

        readonly NetworkVariable<bool> isOpen = new NetworkVariable<bool>(false);

        public bool IsOpen => isOpen.Value;
        /// <summary>Kapı açıklığının ortası (dünya).</summary>
        public Vector3 PassPoint => passPoint != null ? passPoint.position : transform.position;

        Quaternion closedRotation;

        void Awake() => closedRotation = transform.localRotation;

        public override void OnNetworkSpawn() { if (!All.Contains(this)) All.Add(this); }
        public override void OnNetworkDespawn() => All.Remove(this);

        public override void OnDestroy()
        {
            All.Remove(this);
            base.OnDestroy();
        }

        /// <summary>Sunucu: kapıyı açar/kapatır (taklitçi için).</summary>
        public void ServerSetOpen(bool open)
        {
            if (!IsServer || !IsSpawned) return;
            if (isOpen.Value != open) isOpen.Value = open;
        }

        void Update()
        {
            var target = closedRotation * Quaternion.Euler(0f, isOpen.Value ? openAngle : 0f, 0f);
            transform.localRotation = Quaternion.Slerp(transform.localRotation, target,
                1f - Mathf.Exp(-speed * Time.deltaTime));
        }

        public string GetPrompt(PlayerInteractor interactor) => isOpen.Value ? "Kapıyı kapat" : "Kapıyı aç";

        public void Interact(PlayerInteractor interactor)
        {
            if (!IsSpawned)
            {
                Debug.LogWarning($"[Kapı] {name} ağda doğmamış (IsSpawned=false), açılamıyor.");
                return;
            }
            ToggleRpc();
        }

        [Rpc(SendTo.Server)]
        void ToggleRpc(RpcParams rpcParams = default)
        {
            var player = NetworkManager.SpawnManager.GetPlayerNetworkObject(rpcParams.Receive.SenderClientId);
            if (player != null && Vector3.Distance(player.transform.position, transform.position) > maxUseDistance)
            {
                Debug.LogWarning($"[Kapı] {name}: oyuncu çok uzak, istek reddedildi.");
                return;
            }
            isOpen.Value = !isOpen.Value;
            PlayerToggled?.Invoke(rpcParams.Receive.SenderClientId, isOpen.Value);
        }
    }
}
