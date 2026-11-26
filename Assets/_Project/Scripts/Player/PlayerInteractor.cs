using MimicGame.Interaction;
using MimicGame.Items;
using UnityEngine;

namespace MimicGame.Player
{
    /// <summary>Bakılan eşya/nesneyi belirler, E'ye basılınca etkileşir.</summary>
    [RequireComponent(typeof(PlayerInputReader))]
    public class PlayerInteractor : MonoBehaviour
    {
        public Transform viewPoint;
        public float range = 2.8f;
        [Tooltip("Eşya hedefleme için temel açı toleransı (derece). Büyük/yakın eşyalar ek tolerans alır.")]
        public float assistAngle = 6f;
        public LayerMask mask = ~0;

        public IInteractable Target { get; private set; }
        public PlayerInventory Inventory { get; private set; }
        public string Prompt => Target?.GetPrompt(this);

        static readonly RaycastHit[] hitBuffer = new RaycastHit[16];

        PlayerInputReader input;

        void Awake()
        {
            input = GetComponent<PlayerInputReader>();
            Inventory = GetComponent<PlayerInventory>();
        }

        void Update()
        {
            Target = null;
            if (viewPoint == null) return;

            Vector3 origin = viewPoint.position;
            Vector3 fwd = viewPoint.forward;

            // Eşya adayları: collider'a değil görsel sınıra göre
            Item bestItem = null;
            float bestAngle = float.MaxValue;
            var items = Item.All;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.IsHeld) continue;

                Bounds b = item.GetVisualBounds();
                float dist = Vector3.Distance(origin, b.ClosestPoint(origin));
                if (dist > range) continue;

                Vector3 toCenter = b.center - origin;
                float centerDist = toCenter.magnitude;
                float angle = Vector3.Angle(fwd, toCenter);
                float allowed = assistAngle
                    + Mathf.Rad2Deg * Mathf.Atan2(b.extents.magnitude, Mathf.Max(centerDist, 0.01f));
                if (angle > allowed) continue;
                if (angle >= bestAngle) continue;

                if (IsBlocked(origin, toCenter, centerDist, item)) continue;

                bestItem = item;
                bestAngle = angle;
            }

            // Diğer etkileşilebilirler (kapı vb.): tek kesin ışın
            IInteractable other = null;
            int count = Physics.RaycastNonAlloc(origin, fwd, hitBuffer, range, mask, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            Collider nearestCol = null;
            for (int i = 0; i < count; i++)
            {
                var h = hitBuffer[i];
                if (h.collider == null || h.collider is CharacterController) continue;
                if (h.distance < nearest)
                {
                    nearest = h.distance;
                    nearestCol = h.collider;
                }
            }
            if (nearestCol != null)
            {
                var candidate = nearestCol.GetComponentInParent<IInteractable>();
                if (candidate != null && !(candidate is Item)) other = candidate;
            }

            // Kesin ışın kapıya vuruyorsa, çok iyi nişan alınmış bir eşya yoksa onu seç
            if (other != null && !(bestItem != null && bestAngle < 4f)) Target = other;
            else if (bestItem != null) Target = bestItem;

            if (Target != null && input.InteractPressed)
                Target.Interact(this);
        }

        /// <summary>Gözden eşyanın merkezine giderken araya başka bir şey giriyor mu?</summary>
        bool IsBlocked(Vector3 origin, Vector3 toCenter, float centerDist, Item item)
        {
            if (centerDist < 0.01f) return false;
            int n = Physics.RaycastNonAlloc(origin, toCenter / centerDist, hitBuffer, centerDist, mask,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = hitBuffer[i];
                if (h.collider == null || h.collider is CharacterController) continue;
                if (h.collider.GetComponentInParent<Item>() == item) continue;
                if (h.distance < centerDist - 0.15f) return true;
            }
            return false;
        }
    }
}
