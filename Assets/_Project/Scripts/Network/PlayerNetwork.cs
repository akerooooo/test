using System.Collections.Generic;
using MimicGame.Items;
using MimicGame.Player;
using MimicGame.UI;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.Rendering;

namespace MimicGame.Network
{
    /// <summary>
    /// Oyuncu prefabının ağ köprüsü. Sahibi dışındakilerde girdi/kamera bileşenlerini kapatır,
    /// bakış açısı ve eğilmeyi senkronlar, doğuşta spawn noktasına ışınlar.
    /// </summary>
    public class PlayerNetwork : NetworkBehaviour
    {
        public static PlayerNetwork Local { get; private set; }
        public static readonly List<PlayerNetwork> All = new List<PlayerNetwork>();

        public Transform cameraPivot;
        public Transform body;
        public Renderer bodyRenderer;
        public Camera cam;
        public AudioListener listener;

        public CharacterController Controller { get; private set; }

        readonly NetworkVariable<float> pitch = new NetworkVariable<float>(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<bool> crouching = new NetworkVariable<bool>(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        PlayerMotor motor;
        PlayerLook look;
        float shownHeight = 1.8f;
        float shownPitch;
        bool pendingTeleport;
        NetworkTransform netTransform;

        void Awake()
        {
            Controller = GetComponent<CharacterController>();
            motor = GetComponent<PlayerMotor>();
            look = GetComponent<PlayerLook>();
            netTransform = GetComponent<NetworkTransform>();
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            // Eşyalar oyuncu kapsülüyle çarpışmasın
            foreach (var item in Item.All)
                if (item != null) item.IgnorePlayer(Controller);

            shownHeight = motor.standHeight;
            shownPitch = pitch.Value;

            if (!IsOwner)
            {
                SetEnabled<PlayerInputReader>(false);
                SetEnabled<PlayerMotor>(false);
                SetEnabled<PlayerLook>(false);
                SetEnabled<PlayerInteractor>(false);
                SetEnabled<PlayerHUD>(false);
                if (cam != null) cam.enabled = false;
                if (listener != null) listener.enabled = false;
                ApplyBody(shownHeight);
                return;
            }

            Local = this;
            if (bodyRenderer != null) bodyRenderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;

            // Spawn noktasına ışınla
            var pos = new Vector3((OwnerClientId % 4) * 1.5f - 2.25f, 0.05f, -3f);
            Controller.enabled = false;
            transform.position = pos;
            Controller.enabled = true;
            pendingTeleport = true; // ağdaki kopyalar kaymadan zıplasın

            MenuCamera.SetActive(false);
            PlayerLook.SetCursorLocked(true);
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);

            // Sunucu: oyuncunun taşıdığı eşyaları ayaklarının altına bırak
            if (IsServer && NetworkManager != null && !NetworkManager.ShutdownInProgress)
            {
                ulong id = NetworkObject.NetworkObjectId;
                var items = Item.All;
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null || item.HolderId != id) continue;
                    var rot = Quaternion.Euler(0f, item.RestRotation.eulerAngles.y, 0f);
                    item.ComputeGroundPose(transform.position + Vector3.up * 0.3f, rot, out var p);
                    item.ServerDrop(p, rot);
                }
            }

            if (Local == this)
            {
                Local = null;
                MenuCamera.SetActive(true);
                PlayerLook.SetCursorLocked(false);
            }
        }

        void SetEnabled<T>(bool on) where T : Behaviour
        {
            var c = GetComponent<T>();
            if (c != null) c.enabled = on;
        }

        void Update()
        {
            if (!IsSpawned) return;

            if (IsOwner)
            {
                if (pendingTeleport && netTransform != null && netTransform.CanCommitToTransform)
                {
                    pendingTeleport = false;
                    netTransform.Teleport(transform.position, transform.rotation, transform.localScale);
                }
                float p = look != null ? look.Pitch : 0f;
                if (Mathf.Abs(p - pitch.Value) > 0.5f) pitch.Value = p;
                if (motor != null && crouching.Value != motor.IsCrouching) crouching.Value = motor.IsCrouching;
                ApplyBody(motor != null ? motor.CurrentHeight : shownHeight);
                return;
            }

            // Uzak oyuncu: eğilme ve bakış açısını uygula
            float target = crouching.Value ? motor.crouchHeight : motor.standHeight;
            shownHeight = Mathf.MoveTowards(shownHeight, target, motor.crouchTransitionSpeed * Time.deltaTime);
            shownPitch = Mathf.Lerp(shownPitch, pitch.Value, 1f - Mathf.Exp(-20f * Time.deltaTime));
            ApplyBody(shownHeight);
            if (cameraPivot != null)
            {
                cameraPivot.localPosition = new Vector3(0f, shownHeight - look.eyeOffsetFromTop, 0f);
                cameraPivot.localRotation = Quaternion.Euler(shownPitch, 0f, 0f);
            }
        }

        // 2 m'lik kapsül primitifini verilen boya göre ölçekler
        void ApplyBody(float height)
        {
            if (body == null) return;
            body.localScale = new Vector3(0.7f, height * 0.5f, 0.7f);
            body.localPosition = new Vector3(0f, height * 0.5f, 0f);
        }
    }
}
