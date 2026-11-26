using System.Collections.Generic;
using MimicGame.Interaction;
using MimicGame.Player;
using UnityEngine;

namespace MimicGame.Items
{
    /// <summary>Yerden alınabilen, değeri ve ağırlığı olan ganimet. Fizik simülasyonu yok.</summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Item : MonoBehaviour, IInteractable
    {
        public string displayName = "Hurda";
        public int value = 10;
        [Tooltip("Kilogram. Taşıyanı yavaşlatır.")]
        public float weight = 2f;

        /// <summary>Sahnedeki tüm eşyalar (ganimet sayımı için).</summary>
        public static readonly List<Item> All = new List<Item>();

        public bool IsHeld { get; private set; }

        static readonly RaycastHit[] hitBuffer = new RaycastHit[16];

        Rigidbody rb;
        Collider[] colliders;
        Renderer[] renderers;

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None; // sahnede Interpolate kayıtlı olabilir; Transform ile taşıyoruz
            colliders = GetComponentsInChildren<Collider>();
            renderers = GetComponentsInChildren<Renderer>();
        }

        void OnEnable()
        {
            All.Add(this);
            foreach (var motor in PlayerMotor.All)
                if (motor != null) IgnorePlayer(motor.Controller);
        }

        void OnDisable() => All.Remove(this);

        void Start() => PlaceOnGround(transform.position, transform.rotation);

        public string GetPrompt(PlayerInteractor interactor) => $"Al: {displayName} ({value}$)";

        public void Interact(PlayerInteractor interactor)
        {
            if (interactor.Inventory != null) interactor.Inventory.TryPickup(this);
        }

        /// <summary>Oyuncu CharacterController'ı ile çarpışmayı kapatır.</summary>
        public void IgnorePlayer(CharacterController controller)
        {
            if (controller == null || colliders == null) return;
            foreach (var c in colliders)
                if (c != null) Physics.IgnoreCollision(c, controller, true);
        }

        public void OnPickedUp(Transform hand)
        {
            IsHeld = true;
            foreach (var c in colliders) c.enabled = false;
            transform.SetParent(hand, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            Physics.SyncTransforms();
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

        /// <summary>Eşyayı verilen noktanın altındaki zemine oturtur.</summary>
        public void PlaceOnGround(Vector3 approxPosition, Quaternion rotation)
        {
            transform.SetParent(null, true);
            transform.SetPositionAndRotation(approxPosition, rotation);
            SetVisible(true); // bounds için renderer açık olmalı

            // Pivottan alt noktaya mesafe
            float minY = float.MaxValue;
            foreach (var r in renderers)
                if (r != null) minY = Mathf.Min(minY, r.bounds.min.y);
            float pivotToBottom = minY == float.MaxValue ? 0f : transform.position.y - minY;

            // En yakın zemin (kendi collider'ları ve oyuncu hariç)
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

            if (found)
                transform.position = point + Vector3.up * (pivotToBottom + 0.005f);

            IsHeld = false;
            foreach (var c in colliders) c.enabled = true;
            Physics.SyncTransforms(); // collider'lar yeni konumda güncel olsun
        }

        public void SetVisible(bool visible)
        {
            foreach (var r in renderers) r.enabled = visible;
        }
    }
}
