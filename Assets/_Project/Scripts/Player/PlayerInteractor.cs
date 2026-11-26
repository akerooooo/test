using MimicGame.Interaction;
using UnityEngine;

namespace MimicGame.Player
{
    /// <summary>Kameranın baktığı yere ışın atar, E'ye basılınca etkileşir.</summary>
    [RequireComponent(typeof(PlayerInputReader))]
    public class PlayerInteractor : MonoBehaviour
    {
        public Transform viewPoint;
        public float range = 3f;
        [Tooltip("Yedek hedefleme için küre yarıçapı.")]
        public float assistRadius = 0.3f;
        [Tooltip("Yedek hedefleme için en büyük açı (derece).")]
        public float assistAngle = 15f;
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

            // Adım 1: kesin ışın
            if (Physics.Raycast(viewPoint.position, viewPoint.forward, out var hit, range, mask,
                    QueryTriggerInteraction.Ignore))
                Target = hit.collider.GetComponentInParent<IInteractable>();

            // Adım 2: küçük eşyalar için toleranslı küre taraması
            if (Target == null) Target = FindAssistTarget();

            if (Target != null && input.InteractPressed)
                Target.Interact(this);
        }

        IInteractable FindAssistTarget()
        {
            Vector3 origin = viewPoint.position;
            Vector3 forward = viewPoint.forward;
            int count = Physics.SphereCastNonAlloc(origin, assistRadius, forward, hitBuffer, range, mask,
                QueryTriggerInteraction.Ignore);

            IInteractable best = null;
            float bestAngle = assistAngle;
            for (int i = 0; i < count; i++)
            {
                var col = hitBuffer[i].collider;
                if (col == null) continue;
                var candidate = col.GetComponentInParent<IInteractable>();
                if (candidate == null) continue;

                Vector3 point = col.bounds.center;
                float angle = Vector3.Angle(forward, point - origin);
                if (angle > assistAngle || angle >= bestAngle) continue;

                // Görüş hattı: araya başka bir şey girmemeli
                if (Physics.Linecast(origin, point, out var block, mask, QueryTriggerInteraction.Ignore)
                    && !ReferenceEquals(block.collider.GetComponentInParent<IInteractable>(), candidate))
                    continue;

                best = candidate;
                bestAngle = angle;
            }
            return best;
        }
    }
}
