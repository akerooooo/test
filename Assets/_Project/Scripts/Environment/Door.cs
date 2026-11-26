using MimicGame.Interaction;
using MimicGame.Player;
using UnityEngine;

namespace MimicGame.Environment
{
    /// <summary>
    /// Menteşe objesine eklenir. Kapı paneli bu objenin çocuğu olmalı.
    /// E ile açılır/kapanır.
    /// </summary>
    public class Door : MonoBehaviour, IInteractable
    {
        public float openAngle = -95f;
        public float speed = 5f;
        public bool isOpen;

        Quaternion closedRotation;

        void Awake() => closedRotation = transform.localRotation;

        void Update()
        {
            var target = closedRotation * Quaternion.Euler(0f, isOpen ? openAngle : 0f, 0f);
            transform.localRotation = Quaternion.Slerp(transform.localRotation, target,
                1f - Mathf.Exp(-speed * Time.deltaTime));
        }

        public string GetPrompt(PlayerInteractor interactor) => isOpen ? "Kapıyı kapat" : "Kapıyı aç";

        public void Interact(PlayerInteractor interactor) => isOpen = !isOpen;
    }
}
