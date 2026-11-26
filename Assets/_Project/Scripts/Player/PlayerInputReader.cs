using UnityEngine;
using UnityEngine.InputSystem;

namespace MimicGame.Player
{
    /// <summary>
    /// Tüm oyuncu girdilerini tek yerden okur. Diğer bileşenler klavye/fareyi
    /// doğrudan değil, bu sınıf üzerinden okur. Böylece ileride ağ (multiplayer)
    /// ve tuş atama eklemek kolay olur.
    /// </summary>
    public class PlayerInputReader : MonoBehaviour
    {
        InputAction move, look, sprint, crouch, jump, interact, drop, flashlight, use, scroll, mute;
        InputAction[] slotKeys;

        public Vector2 Move => move.ReadValue<Vector2>();
        public Vector2 Look => look.ReadValue<Vector2>();
        public bool SprintHeld => sprint.IsPressed();
        public bool CrouchPressed => crouch.WasPressedThisFrame();
        public bool JumpPressed => jump.WasPressedThisFrame();
        public bool InteractPressed => interact.WasPressedThisFrame();
        public bool InteractHeld => interact.IsPressed();
        public bool DropPressed => drop.WasPressedThisFrame();
        public bool FlashlightPressed => flashlight.WasPressedThisFrame();
        public bool UsePressed => use.WasPressedThisFrame();
        public bool MutePressed => mute.WasPressedThisFrame();
        public float Scroll => scroll.ReadValue<float>();

        /// <summary>Bu karede basılan envanter slotu (0-3), yoksa -1.</summary>
        public int SlotPressed
        {
            get
            {
                for (int i = 0; i < slotKeys.Length; i++)
                    if (slotKeys[i].WasPressedThisFrame()) return i;
                return -1;
            }
        }

        void Awake()
        {
            move = new InputAction("Move", InputActionType.Value);
            move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            move.AddBinding("<Gamepad>/leftStick");

            look = new InputAction("Look", InputActionType.Value, "<Mouse>/delta");
            sprint = Button("Sprint", "<Keyboard>/leftShift");
            crouch = Button("Crouch", "<Keyboard>/leftCtrl");
            crouch.AddBinding("<Keyboard>/c");
            jump = Button("Jump", "<Keyboard>/space");
            interact = Button("Interact", "<Keyboard>/e");
            drop = Button("Drop", "<Keyboard>/g");
            flashlight = Button("Flashlight", "<Keyboard>/f");
            use = Button("Use", "<Mouse>/leftButton");
            mute = Button("Mute", "<Keyboard>/m");
            scroll = new InputAction("Scroll", InputActionType.Value, "<Mouse>/scroll/y");

            slotKeys = new InputAction[4];
            for (int i = 0; i < slotKeys.Length; i++)
                slotKeys[i] = Button("Slot" + (i + 1), "<Keyboard>/" + (i + 1));
        }

        static InputAction Button(string name, string binding) =>
            new InputAction(name, InputActionType.Button, binding);

        void OnEnable() { foreach (var a in All()) a.Enable(); }
        void OnDisable() { foreach (var a in All()) a.Disable(); }
        void OnDestroy() { foreach (var a in All()) a.Dispose(); }

        System.Collections.Generic.IEnumerable<InputAction> All()
        {
            if (move == null) yield break;
            yield return move; yield return look; yield return sprint; yield return crouch;
            yield return jump; yield return interact; yield return drop; yield return flashlight;
            yield return use; yield return scroll; yield return mute;
            foreach (var s in slotKeys) yield return s;
        }
    }
}
