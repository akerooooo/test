using System.Collections.Generic;
using MimicGame.Interaction;
using MimicGame.Network;
using MimicGame.Player;
using Unity.Netcode;
using UnityEngine;

namespace MimicGame.Items
{
    /// <summary>
    /// Yerden alınabilen, değeri ve ağırlığı olan ganimet. Fizik yok; durumu sunucu tutar.
    /// Tutulurken eşya elin pozunu LateUpdate'te takip eder (reparent yok).
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class Item : NetworkBehaviour, IInteractable
    {
        public string displayName = "Hurda";
        public int value = 10;
        [Tooltip("Kilogram. Taşıyanı yavaşlatır.")]
        public float weight = 2f;

        /// <summary>Sahnedeki tüm eşyalar (ganimet sayımı için).</summary>
        public static readonly List<Item> All = new List<Item>();

        public const ulong NoHolder = ulong.MaxValue;

        public bool IsHeld => holderId.Value != NoHolder;
        public ulong HolderId => holderId.Value;
        public int HeldSlot => heldSlot.Value;
        public Vector3 RestPosition => restPosition.Value;
        public Quaternion RestRotation => restRotation.Value;

        // Hepsi sunucu yazar
        readonly NetworkVariable<ulong> holderId = new NetworkVariable<ulong>(NoHolder);
        readonly NetworkVariable<int> heldSlot = new NetworkVariable<int>(-1);
        readonly NetworkVariable<Vector3> restPosition = new NetworkVariable<Vector3>();
        readonly NetworkVariable<Quaternion> restRotation = new NetworkVariable<Quaternion>(Quaternion.identity);

        static readonly RaycastHit[] hitBuffer = new RaycastHit[16];

        Collider[] colliders;
        Renderer[] renderers;
        bool wasHeld = true; // ilk karede poz uygulansın
        bool visible = true;
        bool collidersOn = true;
        ulong cachedHolderId = NoHolder;
        PlayerInventory cachedInventory;

        void Awake()
        {
            // Eski sahnelerden kalan Rigidbody varsa kinematik tut
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.interpolation = RigidbodyInterpolation.None;
            }
            colliders = GetComponentsInChildren<Collider>();
            renderers = GetComponentsInChildren<Renderer>();
        }

        void OnEnable()
        {
            All.Add(this);
            foreach (var p in PlayerNetwork.All)
                if (p != null) IgnorePlayer(p.Controller);
            foreach (var motor in PlayerMotor.All)
                if (motor != null) IgnorePlayer(motor.Controller);
        }

        void OnDisable() => All.Remove(this);

        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;
            ComputeGroundPose(transform.position, transform.rotation, out var pos);
            restPosition.Value = pos;
            restRotation.Value = transform.rotation;
        }

        public string GetPrompt(PlayerInteractor interactor) =>
            IsHeld ? null : $"Al: {displayName} ({value}$)";

        public void Interact(PlayerInteractor interactor)
        {
            if (interactor.Inventory != null) interactor.Inventory.RequestPickup(this);
        }

        // ---------- Sunucu ----------

        public void ServerSetHeld(ulong holder, int slot)
        {
            if (!IsServer) return;
            heldSlot.Value = slot;
            holderId.Value = holder;
        }

        public void ServerDrop(Vector3 pos, Quaternion rot)
        {
            if (!IsServer) return;
            restPosition.Value = pos;
            restRotation.Value = rot;
            holderId.Value = NoHolder;
            heldSlot.Value = -1;
        }

        // ---------- Poz ----------

        void LateUpdate()
        {
            if (!IsSpawned) return;

            if (IsHeld)
            {
                var inv = ResolveHolder();
                if (inv == null || inv.hand == null)
                {
                    SetVisible(false);
                    SetCollidersEnabled(false);
                }
                else
                {
                    transform.SetPositionAndRotation(inv.hand.position, inv.hand.rotation);
                    SetVisible(inv.SelectedSlot == heldSlot.Value);
                    SetCollidersEnabled(false);
                }
                wasHeld = true;
                return;
            }

            Vector3 rp = restPosition.Value;
            Quaternion rr = restRotation.Value;
            bool moved = (transform.position - rp).sqrMagnitude > 1e-8f ||
                         Quaternion.Angle(transform.rotation, rr) > 0.01f;
            if (moved) transform.SetPositionAndRotation(rp, rr);
            SetVisible(true);
            SetCollidersEnabled(true);
            if (wasHeld || moved)
            {
                // Elden bırakılınca (veya ilk karede) collider'lar yeni konumda güncel olsun
                Physics.SyncTransforms();
                wasHeld = false;
            }
        }

        PlayerInventory ResolveHolder()
        {
            ulong id = holderId.Value;
            if (cachedInventory != null && cachedHolderId == id) return cachedInventory;
            cachedInventory = null;
            cachedHolderId = id;
            var nm = NetworkManager;
            if (nm != null && nm.SpawnManager != null &&
                nm.SpawnManager.SpawnedObjects.TryGetValue(id, out var no) && no != null)
                cachedInventory = no.GetComponent<PlayerInventory>();
            return cachedInventory;
        }

        void SetVisible(bool v)
        {
            if (visible == v) return;
            visible = v;
            foreach (var r in renderers) if (r != null) r.enabled = v;
        }

        void SetCollidersEnabled(bool on)
        {
            if (collidersOn == on) return;
            collidersOn = on;
            foreach (var c in colliders) if (c != null) c.enabled = on;
        }

        /// <summary>Oyuncu CharacterController'ı ile çarpışmayı kapatır.</summary>
        public void IgnorePlayer(CharacterController controller)
        {
            if (controller == null || colliders == null) return;
            foreach (var c in colliders)
                if (c != null) Physics.IgnoreCollision(c, controller, true);
        }

        /// <summary>Tüm renderer'ların birleşik sınırı (hedefleme için).</summary>
        public Bounds GetVisualBounds()
        {
            bool has = false;
            Bounds b = default;
            if (renderers != null)
            {
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    if (!has) { b = r.bounds; has = true; }
                    else b.Encapsulate(r.bounds);
                }
            }
            return has ? b : new Bounds(transform.position, Vector3.one * 0.2f);
        }

        /// <summary>
        /// Verilen noktanın altındaki zemine oturan poz (transform'u oynatmaz).
        /// Zemin bulunamazsa false döner ve pos = approxPosition olur.
        /// </summary>
        public bool ComputeGroundPose(Vector3 approxPosition, Quaternion rotation, out Vector3 pos)
        {
            pos = approxPosition;
            if (renderers == null) { renderers = GetComponentsInChildren<Renderer>(); colliders = GetComponentsInChildren<Collider>(); }

            // Pivottan alt noktaya mesafe: renderer yerel sınırlarını yeni poza taşıyarak
            var toNew = Matrix4x4.TRS(approxPosition, rotation, transform.lossyScale) * transform.worldToLocalMatrix;
            float minY = float.MaxValue;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                var lb = r.localBounds;
                var m = toNew * r.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var corner = lb.center + new Vector3(
                        (i & 1) == 0 ? -lb.extents.x : lb.extents.x,
                        (i & 2) == 0 ? -lb.extents.y : lb.extents.y,
                        (i & 4) == 0 ? -lb.extents.z : lb.extents.z);
                    minY = Mathf.Min(minY, m.MultiplyPoint3x4(corner).y);
                }
            }
            float pivotToBottom = minY == float.MaxValue ? 0f : approxPosition.y - minY;

            // En yakın zemin (kendi collider'ları ve oyuncular hariç)
            int count = Physics.RaycastNonAlloc(approxPosition + Vector3.up * 0.5f, Vector3.down, hitBuffer,
                5f, ~0, QueryTriggerInteraction.Ignore);
            bool found = false;
            float best = float.MaxValue;
            Vector3 point = approxPosition;
            for (int i = 0; i < count; i++)
            {
                var hit = hitBuffer[i];
                if (hit.collider is CharacterController) continue;
                if (System.Array.IndexOf(colliders, hit.collider) >= 0) continue;
                if (hit.distance < best)
                {
                    best = hit.distance;
                    point = hit.point;
                    found = true;
                }
            }

            if (found) pos = point + Vector3.up * (pivotToBottom + 0.005f);
            return found;
        }
    }
}
