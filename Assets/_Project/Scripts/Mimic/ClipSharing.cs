using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace MimicGame.Mimic
{
    /// <summary>
    /// Oyuncu prefabında: sahibin "sesim taklit edilebilir" gizlilik tercihini herkese duyurur.
    /// Klipler artık ağdan yollanmaz; her makine canlı ses paketlerinden kendi kütüphanesini kurar.
    /// </summary>
    public class ClipSharing : NetworkBehaviour
    {
        const string PrefKey = "AllowMimicry";

        static int allowCache = -1;
        static ClipSharing localOwner;
        static readonly Dictionary<ulong, ClipSharing> byClient = new Dictionary<ulong, ClipSharing>();

        /// <summary>Sesimin taklit edilmesine izin ver (menüdeki gizlilik ayarı).</summary>
        public static bool AllowMimicry
        {
            get
            {
                if (allowCache < 0) allowCache = PlayerPrefs.GetInt(PrefKey, 1) != 0 ? 1 : 0;
                return allowCache != 0;
            }
            set
            {
                allowCache = value ? 1 : 0;
                PlayerPrefs.SetInt(PrefKey, allowCache);
                PlayerPrefs.Save();
                // Yerel sahip varsa ağ değişkenini de güncelle
                if (localOwner != null && localOwner.IsSpawned && localOwner.IsOwner)
                    localOwner.allowMimicry.Value = value;
            }
        }

        // Sahip yazar; doğuşta sahip kendi tercihini yazana kadar kapalı varsayılır
        readonly NetworkVariable<bool> allowMimicry = new NetworkVariable<bool>(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        /// <summary>Bu oyuncunun sesi taklit edilebilir mi? Bilinmiyorsa false.</summary>
        public static bool IsAllowed(ulong clientId)
            => byClient.TryGetValue(clientId, out var cs) && cs != null && cs.allowMimicry.Value;

        public override void OnNetworkSpawn()
        {
            byClient[OwnerClientId] = this;
            if (IsOwner)
            {
                localOwner = this;
                allowMimicry.Value = AllowMimicry;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (byClient.TryGetValue(OwnerClientId, out var cs) && cs == this) byClient.Remove(OwnerClientId);
            if (localOwner == this) localOwner = null;
            // Yerel oyuncu çıkınca (bağlantı bitti) kütüphaneyi temizle
            if (IsOwner)
            {
                VoiceClipLibrary.Clear();
                ClipRecorder.Reset();
            }
        }
    }
}
