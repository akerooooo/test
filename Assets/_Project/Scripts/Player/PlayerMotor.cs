using System.Collections.Generic;
using MimicGame.Items;
using UnityEngine;

namespace MimicGame.Player
{
    /// <summary>
    /// Birinci şahıs hareket: yürüme, koşma (dayanıklılık), eğilme, zıplama.
    /// Hız/eğilme/zıplama bilgileri dışarı açık; ileride taklitçinin
    /// "hareket parmak izi" bu değerlerden çıkarılacak.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(PlayerInputReader))]
    public class PlayerMotor : MonoBehaviour
    {
        [Header("Hız (m/sn)")]
        public float walkSpeed = 3.5f;
        public float sprintSpeed = 6f;
        public float crouchSpeed = 1.8f;
        public float acceleration = 12f;

        [Header("Zıplama / Yerçekimi")]
        public float jumpHeight = 0.9f;
        public float gravity = -20f;

        [Header("Eğilme")]
        public float standHeight = 1.8f;
        public float crouchHeight = 1.0f;
        public float crouchTransitionSpeed = 6f;

        [Header("Dayanıklılık (sn koşu)")]
        public float maxStamina = 5f;
        public float staminaRegenPerSecond = 1.2f;
        public float staminaRegenDelay = 1f;

        /// <summary>Taşınan ağırlığa göre envanterin ayarladığı hız çarpanı.</summary>
        public float CarryWeightMultiplier { get; set; } = 1f;

        public bool IsCrouching { get; private set; }
        public bool IsSprinting { get; private set; }
        public bool IsGrounded => cc.isGrounded;
        public bool IsExhausted { get; private set; }
        public Vector3 HorizontalVelocity => horizontalVelocity;
        public float Stamina01 => stamina / maxStamina;
        public float CurrentHeight => cc.height;
        public event System.Action Jumped;

        /// <summary>Sahnedeki oyuncu hareketleri (eşya çarpışma yoksayma için).</summary>
        public static readonly List<PlayerMotor> All = new List<PlayerMotor>();

        public CharacterController Controller => cc != null ? cc : (cc = GetComponent<CharacterController>());

        CharacterController cc;
        PlayerInputReader input;
        Vector3 horizontalVelocity;
        float verticalVelocity;
        float stamina;
        float regenTimer;

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            input = GetComponent<PlayerInputReader>();
            stamina = maxStamina;
            SetHeight(standHeight);
        }

        void OnEnable()
        {
            All.Add(this);
            // Mevcut eşyalarla çarpışmayı kapat
            foreach (var item in Item.All)
                if (item != null) item.IgnorePlayer(Controller);
        }

        void OnDisable() => All.Remove(this);

        void Update()
        {
            float dt = Time.deltaTime;
            UpdateCrouch(dt);

            Vector2 m = input.Move;
            Vector3 wish = transform.right * m.x + transform.forward * m.y;
            if (wish.sqrMagnitude > 1f) wish.Normalize();

            IsSprinting = input.SprintHeld && m.y > 0.1f && !IsCrouching && !IsExhausted;
            UpdateStamina(dt);

            float speed = IsCrouching ? crouchSpeed : IsSprinting ? sprintSpeed : walkSpeed;
            speed *= CarryWeightMultiplier;

            float accel = cc.isGrounded ? acceleration : acceleration * 0.25f;
            horizontalVelocity = Vector3.Lerp(horizontalVelocity, wish * speed, 1f - Mathf.Exp(-accel * dt));

            if (cc.isGrounded)
            {
                if (verticalVelocity < 0f) verticalVelocity = -2f;
                if (input.JumpPressed && !IsCrouching)
                {
                    verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
                    Jumped?.Invoke();
                }
            }
            verticalVelocity += gravity * dt;

            var flags = cc.Move((horizontalVelocity + Vector3.up * verticalVelocity) * dt);
            if ((flags & CollisionFlags.Above) != 0 && verticalVelocity > 0f) verticalVelocity = 0f;
        }

        void UpdateStamina(float dt)
        {
            if (IsSprinting && horizontalVelocity.sqrMagnitude > 1f)
            {
                stamina -= dt;
                regenTimer = staminaRegenDelay;
                if (stamina <= 0f)
                {
                    stamina = 0f;
                    IsExhausted = true;
                    IsSprinting = false;
                }
            }
            else
            {
                regenTimer -= dt;
                if (regenTimer <= 0f)
                    stamina = Mathf.Min(maxStamina, stamina + staminaRegenPerSecond * dt);
                if (IsExhausted && stamina >= maxStamina * 0.3f) IsExhausted = false;
            }
        }

        void UpdateCrouch(float dt)
        {
            if (input.CrouchPressed)
            {
                if (!IsCrouching) IsCrouching = true;
                else if (CanStand()) IsCrouching = false;
            }

            float target = IsCrouching ? crouchHeight : standHeight;
            if (!Mathf.Approximately(cc.height, target))
                SetHeight(Mathf.MoveTowards(cc.height, target, crouchTransitionSpeed * dt));
        }

        bool CanStand()
        {
            float r = cc.radius * 0.95f;
            Vector3 origin = transform.position + Vector3.up * (cc.height - cc.radius);
            float distance = standHeight - cc.height + 0.05f;
            return !Physics.SphereCast(origin, r, Vector3.up, out _, distance, ~0, QueryTriggerInteraction.Ignore);
        }

        void SetHeight(float h)
        {
            cc.height = h;
            cc.center = Vector3.up * (h * 0.5f);
        }
    }
}
