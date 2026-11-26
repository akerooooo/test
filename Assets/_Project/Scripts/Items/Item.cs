using System.Collections.Generic;
using MimicGame.Interaction;
using MimicGame.Player;
using UnityEngine;

namespace MimicGame.Items
{
    /// <summary>Yerden alınabilen, değeri ve ağırlığı olan ganimet.</summary>
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

        Rigidbody rb;
        Collider[] colliders;
        Renderer[] renderers;

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            colliders = GetComponentsInChildren<Collider>();
            renderers = GetComponentsInChildren<Renderer>();
        }

        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        public string GetPrompt(PlayerInteractor interactor) => $"Al: {displayName} ({value}$)";

        public void Interact(PlayerInteractor interactor)
        {
            if (interactor.Inventory != null) interactor.Inventory.TryPickup(this);
        }

        public void OnPickedUp(Transform hand)
        {
            IsHeld = true;
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.None;
            foreach (var c in colliders) c.enabled = false;
            transform.SetParent(hand, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
        }

        public void OnDropped(Vector3 position, Vector3 velocity) =>
            OnDropped(position, transform.rotation, velocity);

        public void OnDropped(Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            transform.SetParent(null, true);
            transform.SetPositionAndRotation(position, rotation);
            IsHeld = false;
            SetVisible(true);
            foreach (var c in colliders) c.enabled = true;
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.linearVelocity = velocity;
            rb.angularVelocity = Vector3.zero;
        }

        /// <summary>Mevcut rotasyonla render sınırlarının yarı yüksekliği (renderer kapalıyken de geçerli).</summary>
        public float GetHalfHeight()
        {
            float h = 0f;
            foreach (var r in renderers)
                if (r != null) h = Mathf.Max(h, r.bounds.extents.y);
            return h;
        }

        public void SetVisible(bool visible)
        {
            foreach (var r in renderers) r.enabled = visible;
        }
    }
}
