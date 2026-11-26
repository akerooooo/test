using MimicGame.Interaction;
using UnityEngine;

namespace MimicGame.Player
{
    /// <summary>Kameranın baktığı yere ışın atar, E'ye basılınca etkileşir.</summary>
    [RequireComponent(typeof(PlayerInputReader))]
    public class PlayerInteractor : MonoBehaviour
    {
        public Transform viewPoint;
        public float range = 2.5f;
        public LayerMask mask = ~0;

        public IInteractable Target { get; private set; }
        public PlayerInventory Inventory { get; private set; }
        public string Prompt => Target?.GetPrompt(this);

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

            if (Physics.Raycast(viewPoint.position, viewPoint.forward, out var hit, range, mask,
                    QueryTriggerInteraction.Ignore))
                Target = hit.collider.GetComponentInParent<IInteractable>();

            if (Target != null && input.InteractPressed)
                Target.Interact(this);
        }
    }
}
