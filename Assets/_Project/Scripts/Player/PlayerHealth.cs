using System.Collections.Generic;
using MimicGame.Network;
using Unity.Netcode;
using UnityEngine;

namespace MimicGame.Player
{
    /// <summary>
    /// Oyuncunun ölü/diri durumu (sunucu yetkili). Ölünce eşyalar düşer, gövde ve fener gizlenir,
    /// sahibi gözlemci kipine geçer. Canlandırma sahibe ışınlama RPC'si yollar.
    /// </summary>
    public class PlayerHealth : NetworkBehaviour
    {
        public static PlayerHealth Local { get; private set; }
        public static readonly List<PlayerHealth> All = new List<PlayerHealth>();

        /// <summary>Gözlemci kamerası (kamera çocuğunda, varsayılan kapalı).</summary>
        public SpectatorCamera spectator;

        readonly NetworkVariable<bool> isDead = new NetworkVariable<bool>(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Bu oyuncu şu an ölü mü.</summary>
        public bool Dead => isDead.Value;

        PlayerNetwork net;

        /// <summary>Verilen sahibin oyuncusu ölü mü (oyuncu yoksa false).</summary>
        public static bool IsDead(ulong clientId)
        {
            for (int i = 0; i < All.Count; i++)
            {
                var h = All[i];
                if (h != null && h.IsSpawned && h.OwnerClientId == clientId) return h.Dead;
            }
            return false;
        }

        /// <summary>Hayatta kalan (doğmuş) oyuncu sayısı.</summary>
        public static int AliveCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < All.Count; i++)
                    if (All[i] != null && All[i].IsSpawned && !All[i].Dead) n++;
                return n;
            }
        }

        void Awake()
        {
            net = GetComponent<PlayerNetwork>();
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            if (IsOwner) Local = this;
            isDead.OnValueChanged += OnDeadChanged;
            if (isDead.Value) Apply(true); // sonradan katılan istemci
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            isDead.OnValueChanged -= OnDeadChanged;
            if (Local == this) Local = null;
            if (spectator != null) spectator.enabled = false;
        }

        void OnDeadChanged(bool oldValue, bool newValue) => Apply(newValue);

        // ---------- Sunucu ----------

        /// <summary>Sunucu: oyuncuyu öldürür, taşıdığı eşyaları ayağının altına bırakır.</summary>
        public void ServerKill(string reason)
        {
            if (!IsServer || !IsSpawned || isDead.Value) return;
            isDead.Value = true;
            PlayerNetwork.ServerDropItemsOf(NetworkObject);
            Debug.Log("[Ölüm] Oyuncu " + OwnerClientId + " öldü: " + reason);
        }

        /// <summary>Sunucu: oyuncuyu canlandırıp verilen konuma ışınlar.</summary>
        public void ServerRevive(Vector3 position)
        {
            if (!IsServer || !IsSpawned) return;
            isDead.Value = false;
            ReviveRpc(position);
            Debug.Log("[Ölüm] Oyuncu " + OwnerClientId + " canlandı: " + position.ToString("F1"));
        }

        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        void ReviveRpc(Vector3 pos)
        {
            if (net != null) net.OwnerTeleport(pos);
        }

        // ---------- Ortak ----------

        // Ölüm durumunu uygular (herkeste); sahibinde kontrol ve kamera da değişir
        void Apply(bool dead)
        {
            if (net != null)
            {
                if (net.bodyRenderer != null) net.bodyRenderer.enabled = !dead;
                if (net.Controller != null) net.Controller.enabled = !dead;
            }
            // Fener: Flashlight.Update ölüyken sahibinde kapatır ve ışığı söndürür

            if (!IsOwner) return;
            SetEnabled<PlayerMotor>(!dead);
            SetEnabled<PlayerInteractor>(!dead);
            SetEnabled<PlayerInventory>(!dead);
            SetEnabled<PlayerLook>(!dead);
            if (spectator != null) spectator.enabled = dead;
        }

        void SetEnabled<T>(bool on) where T : Behaviour
        {
            var c = GetComponent<T>();
            if (c != null) c.enabled = on;
        }
    }
}
