using MimicGame.Items;
using Unity.Netcode;
using UnityEngine;

namespace MimicGame.Player
{
    /// <summary>
    /// 4 slotluk envanter. 1-4 veya fare tekerleği ile slot seçilir, G ile bırakılır.
    /// Slotlar eşyaların (holderId, heldSlot) durumundan türetilir; sunucu belirler.
    /// Taşınan ağırlık oyuncuyu yavaşlatır.
    /// </summary>
    [RequireComponent(typeof(PlayerInputReader))]
    public class PlayerInventory : NetworkBehaviour
    {
        public const int SlotCount = 4;
        const float MaxPickupDistance = 4f;
        const float MaxDropDistance = 5f;

        public Transform viewPoint;
        public Transform hand;
        [Tooltip("Her kg için hız kaybı oranı.")]
        public float slowdownPerKg = 0.008f;
        public float minSpeedMultiplier = 0.5f;

        public int SelectedSlot => selectedSlot.Value;
        public Item Current => GetSlot(SelectedSlot);
        public float TotalWeight { get; private set; }

        readonly NetworkVariable<int> selectedSlot = new NetworkVariable<int>(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        PlayerInputReader input;
        PlayerMotor motor;
        PlayerHealth health;

        void Awake()
        {
            input = GetComponent<PlayerInputReader>();
            motor = GetComponent<PlayerMotor>();
            health = GetComponent<PlayerHealth>();
        }

        /// <summary>Bu oyuncunun verilen slottaki eşyası (yoksa null).</summary>
        public Item GetSlot(int index)
        {
            if (!IsSpawned) return null;
            ulong id = NetworkObject.NetworkObjectId;
            var all = Item.All;
            for (int i = 0; i < all.Count; i++)
            {
                var it = all[i];
                if (it != null && it.HolderId == id && it.HeldSlot == index) return it;
            }
            return null;
        }

        void Update()
        {
            if (!IsSpawned || !IsOwner) return;

            int key = input.SlotPressed;
            if (key >= 0) selectedSlot.Value = key;

            float scroll = input.Scroll;
            if (Mathf.Abs(scroll) > 0.01f)
                selectedSlot.Value = (SelectedSlot + (scroll > 0f ? -1 : 1) + SlotCount) % SlotCount;

            if (input.DropPressed) DropCurrent();

            RecalculateWeight();
        }

        /// <summary>Sahip tarafı: eşyayı almak için sunucudan izin ister.</summary>
        public void RequestPickup(Item item)
        {
            if (!IsOwner || item == null || item.IsHeld || !item.IsSpawned) return;

            int slot = GetSlot(SelectedSlot) == null ? SelectedSlot : -1;
            for (int i = 0; slot < 0 && i < SlotCount; i++)
                if (GetSlot(i) == null) slot = i;
            if (slot < 0) return; // envanter dolu

            selectedSlot.Value = slot;
            PickupRpc(new NetworkObjectReference(item.NetworkObject), slot);
        }

        void DropCurrent()
        {
            var item = Current;
            if (item == null) return;

            // Yatay ileri yön (kamera eğimi yok sayılır)
            Vector3 fwd = Vector3.ProjectOnPlane(viewPoint.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.0001f) fwd = transform.forward;
            fwd.Normalize();

            // Duvara çarpıyorsa mesafeyi kısalt
            float distance = 0.8f;
            if (Physics.Raycast(transform.position + Vector3.up * 0.5f, fwd, out var wall, distance, ~0,
                    QueryTriggerInteraction.Ignore))
                distance = Mathf.Max(0f, wall.distance - 0.25f);

            Quaternion rot = Quaternion.Euler(0f, Quaternion.LookRotation(fwd, Vector3.up).eulerAngles.y, 0f);
            item.FindFreeGroundPose(transform.position + fwd * distance + Vector3.up * 0.3f, rot, out var pos);
            DropRpc(new NetworkObjectReference(item.NetworkObject), pos, rot);
        }

        void RecalculateWeight()
        {
            float w = 0f;
            for (int i = 0; i < SlotCount; i++)
            {
                var it = GetSlot(i);
                if (it != null) w += it.weight;
            }
            TotalWeight = w;
            if (motor != null)
                motor.CarryWeightMultiplier = Mathf.Clamp(1f - w * slowdownPerKg, minSpeedMultiplier, 1f);
        }

        // ---------- Sunucu ----------

        [Rpc(SendTo.Server)]
        void PickupRpc(NetworkObjectReference itemRef, int slot, RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
            if (health != null && health.Dead) return; // ölüler eşya alamaz
            if (slot < 0 || slot >= SlotCount) return;
            if (!itemRef.TryGet(out var no) || no == null) return;
            var item = no.GetComponent<Item>();
            if (item == null || item.IsHeld) return;
            if (GetSlot(slot) != null) return;
            if (Vector3.Distance(transform.position, item.RestPosition) > MaxPickupDistance) return;

            item.ServerSetHeld(NetworkObject.NetworkObjectId, slot);
        }

        [Rpc(SendTo.Server)]
        void DropRpc(NetworkObjectReference itemRef, Vector3 pos, Quaternion rot, RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != OwnerClientId) return;
            if (!itemRef.TryGet(out var no) || no == null) return;
            var item = no.GetComponent<Item>();
            if (item == null || item.HolderId != NetworkObject.NetworkObjectId) return;

            // Uzak/geçersiz konumu oyuncunun ayağına çek
            if (float.IsNaN(pos.x + pos.y + pos.z) ||
                Vector3.Distance(transform.position, pos) > MaxDropDistance)
                item.FindFreeGroundPose(transform.position + Vector3.up * 0.3f, rot, out pos);

            item.ServerDrop(pos, rot);
        }
    }
}
