using MimicGame.Network;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MimicGame.Player
{
    /// <summary>Fare ile bakış. Gövde yatayda, kamera dikeyde döner.</summary>
    [RequireComponent(typeof(PlayerInputReader))]
    public class PlayerLook : MonoBehaviour
    {
        public Transform cameraPivot;
        public float sensitivity = 0.08f;
        public float minPitch = -85f;
        public float maxPitch = 85f;
        [Tooltip("Göz hizası, kapsül tepesinden bu kadar aşağıda.")]
        public float eyeOffsetFromTop = 0.15f;

        public float Pitch => pitch;

        PlayerInputReader input;
        PlayerMotor motor;
        float pitch;

        void Awake()
        {
            input = GetComponent<PlayerInputReader>();
            motor = GetComponent<PlayerMotor>();
        }

        void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) SetCursorLocked(false);
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                if (mouse != null && mouse.leftButton.wasPressedThisFrame &&
                    !NetworkMenu.PointerOverButton(mouse.position.ReadValue()))
                    SetCursorLocked(true);
                return;
            }

            Vector2 d = input.Look * sensitivity;
            transform.Rotate(0f, d.x, 0f);
            pitch = Mathf.Clamp(pitch - d.y, minPitch, maxPitch);
            cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        void LateUpdate()
        {
            if (motor != null && cameraPivot != null)
                cameraPivot.localPosition = new Vector3(0f, motor.CurrentHeight - eyeOffsetFromTop, 0f);
        }

        /// <summary>İmleç kilidi; ilk kilit oyuncu doğunca PlayerNetwork tarafından verilir.</summary>
        public static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
