using System.Collections.Generic;
using MimicGame.Items;
using UnityEngine;

namespace MimicGame.Player
{
    /// <summary>
    /// 4 slotluk envanter. 1-4 veya fare tekerleği ile slot seçilir, G ile bırakılır.
    /// Taşınan ağırlık oyuncuyu yavaşlatır.
    /// </summary>
    [RequireComponent(typeof(PlayerInputReader))]
    public class PlayerInventory : MonoBehaviour
    {
        public const int SlotCount = 4;

        public Transform viewPoint;
        public Transform hand;
        [Tooltip("Her kg için hız kaybı oranı.")]
        public float slowdownPerKg = 0.008f;
        public float minSpeedMultiplier = 0.5f;

        public int Selected { get; private set; }
        public Item Current => slots[Selected];
        public IReadOnlyList<Item> Slots => slots;
        public float TotalWeight { get; private set; }

        readonly Item[] slots = new Item[SlotCount];
        PlayerInputReader input;
        PlayerMotor motor;

        void Awake()
        {
            input = GetComponent<PlayerInputReader>();
            motor = GetComponent<PlayerMotor>();
        }

        void Update()
        {
            int key = input.SlotPressed;
            if (key >= 0) Select(key);

            float scroll = input.Scroll;
            if (Mathf.Abs(scroll) > 0.01f)
                Select((Selected + (scroll > 0f ? -1 : 1) + SlotCount) % SlotCount);

            if (input.DropPressed) DropCurrent();
        }

        public bool TryPickup(Item item)
        {
            if (item == null || item.IsHeld) return false;

            int index = slots[Selected] == null ? Selected : System.Array.IndexOf(slots, null);
            if (index < 0) return false; // envanter dolu

            slots[index] = item;
            item.OnPickedUp(hand);
            Select(index);
            RecalculateWeight();
            return true;
        }

        public void DropCurrent()
        {
            var item = Current;
            if (item == null) return;
            slots[Selected] = null;

            // Yatay ileri yön (kamera eğimi yok sayılır)
            Vector3 fwd = Vector3.ProjectOnPlane(viewPoint.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.0001f) fwd = transform.forward;
            fwd.Normalize();

            // Duvara çarpıyorsa mesafeyi kısalt
            float distance = 0.8f;
            if (Physics.Raycast(transform.position + Vector3.up * 0.5f, fwd, out var wall, distance, ~0,
                    QueryTriggerInteraction.Ignore))
                distance = Mathf.Max(0f, wall.distance - 0.25f);

            // Yaw rotasyonu; zemine oturtma Item içinde
            Quaternion rot = Quaternion.Euler(0f, Quaternion.LookRotation(fwd, Vector3.up).eulerAngles.y, 0f);
            item.PlaceOnGround(transform.position + fwd * distance + Vector3.up * 0.3f, rot);
            RecalculateWeight();
        }

        void Select(int index)
        {
            Selected = index;
            for (int i = 0; i < SlotCount; i++)
                if (slots[i] != null) slots[i].SetVisible(i == Selected);
        }

        void RecalculateWeight()
        {
            float w = 0f;
            foreach (var s in slots) if (s != null) w += s.weight;
            TotalWeight = w;
            if (motor != null)
                motor.CarryWeightMultiplier = Mathf.Clamp(1f - w * slowdownPerKg, minSpeedMultiplier, 1f);
        }
    }
}
