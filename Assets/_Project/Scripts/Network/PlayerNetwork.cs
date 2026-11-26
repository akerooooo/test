using System.Collections.Generic;
using MimicGame.Items;
using MimicGame.Level;
using MimicGame.Mimic;
using MimicGame.Player;
using MimicGame.UI;
using MimicGame.Voice;
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
        public PlayerHealth Health { get; private set; }

        /// <summary>Oyuncu ölü mü (PlayerHealth yoksa false).</summary>
        public bool IsDead => Health != null && Health.Dead;

        readonly NetworkVariable<float> pitch = new NetworkVariable<float>(0f,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<bool> crouching = new NetworkVariable<bool>(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        // Her zıplamada sahip artırır (sunucu davranış kaydı + uzak oyuncuda sıçrama görseli)
        readonly NetworkVariable<int> jumpCount = new NetworkVariable<int>(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // Tesisin içinde mi (sahip yazar; ışınlanınca ve her karede yüksekliğe göre güncellenir)
        readonly NetworkVariable<bool> isInside = new NetworkVariable<bool>(false,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        readonly NetworkVariable<int> colorIndex = new NetworkVariable<int>(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Sunucudaki/uzak oyuncuda senkron eğilme durumu.</summary>
        public bool IsCrouchingNet => crouching.Value;
        public int ColorIndex => colorIndex.Value;
        /// <summary>Senkron bakış eğimi (derece).</summary>
        public float Pitch => pitch.Value;
        /// <summary>Toplam zıplama sayısı (sahip artırır).</summary>
        public int JumpCount => jumpCount.Value;
        /// <summary>Oyuncu tesisin içinde mi (iç mekân haritanın altındadır).</summary>
        public bool IsInside => isInside.Value;
        public Color BodyColor => PlayerAppearance.GetColor(colorIndex.Value);

        PlayerMotor motor;
        PlayerLook look;
        float shownHeight = 1.8f;
        float shownPitch;
        bool pendingTeleport;
        NetworkTransform netTransform;
        float hopT = 99f;

        void Awake()
        {
            Controller = GetComponent<CharacterController>();
            motor = GetComponent<PlayerMotor>();
            look = GetComponent<PlayerLook>();
            netTransform = GetComponent<NetworkTransform>();
            Health = GetComponent<PlayerHealth>();
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            // Renk: sunucu atar, herkes uygular
            if (IsServer) colorIndex.Value = (int)(OwnerClientId % (ulong)PlayerAppearance.Palette.Length);
            colorIndex.OnValueChanged += OnColorChanged;
            jumpCount.OnValueChanged += OnJumpChanged;
            if (IsOwner && motor != null) motor.Jumped += OnOwnerJumped;
            PlayerAppearance.ApplyColor(bodyRenderer, colorIndex.Value);
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
            // Oda yankısı: yalnızca yerel dinleyicide (kamera üstünde)
            RoomAcoustics.Attach(listener != null ? listener.gameObject : (cam != null ? cam.gameObject : null));
            if (bodyRenderer != null) bodyRenderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;

            // Spawn noktasına ışınla
            var pos = new Vector3((OwnerClientId % 4) * 1.5f - 2.25f, 0.05f, -3f);
            OwnerTeleport(pos);

            MenuCamera.SetActive(false);
            PlayerLook.SetCursorLocked(true);
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            colorIndex.OnValueChanged -= OnColorChanged;
            jumpCount.OnValueChanged -= OnJumpChanged;
            if (motor != null) motor.Jumped -= OnOwnerJumped;

            // Sunucu: oyuncunun taşıdığı eşyaları ayaklarının altına bırak
            if (IsServer && NetworkManager != null && !NetworkManager.ShutdownInProgress)
                ServerDropItemsOf(NetworkObject);

            if (Local == this)
            {
                RoomAcoustics.Detach(listener != null ? listener.gameObject : (cam != null ? cam.gameObject : null));
                Local = null;
                MenuCamera.SetActive(true);
                PlayerLook.SetCursorLocked(false);
            }
        }

        /// <summary>Sunucu: oyuncunun taşıdığı tüm eşyaları ayaklarının altına bırakır (ölüm / ayrılma).</summary>
        public static void ServerDropItemsOf(NetworkObject player)
        {
            if (player == null) return;
            ulong id = player.NetworkObjectId;
            Vector3 feet = player.transform.position;
            var items = Item.All;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.HolderId != id) continue;
                var rot = Quaternion.Euler(0f, item.RestRotation.eulerAngles.y, 0f);
                item.ComputeGroundPose(feet + Vector3.up * 0.3f, rot, out var p);
                item.ServerDrop(p, rot);
            }
        }

        /// <summary>Sahip: oyuncuyu ışınlar (kontrolcü kapatılıp açılır) ve ağ kopyalarına anında bildirir.</summary>
        public void OwnerTeleport(Vector3 pos) => DoTeleport(pos, null);

        /// <summary>Sahip: oyuncuyu verilen konuma ve yatay yöne (yaw, derece) ışınlar.</summary>
        public void OwnerTeleport(Vector3 pos, float yaw) => DoTeleport(pos, yaw);

        void DoTeleport(Vector3 pos, float? yaw)
        {
            if (Controller != null) Controller.enabled = false;
            transform.position = pos;
            if (yaw.HasValue) transform.rotation = Quaternion.Euler(0f, yaw.Value, 0f);
            // Ölüyken kontrolcü kapalı kalır (PlayerHealth açar)
            if (Controller != null) Controller.enabled = !IsDead;
            pendingTeleport = true; // ağdaki kopyalar kaymadan zıplasın
            SyncInside(pos);
        }

        // İç mekân durumunu konumdan türetir (yalnızca sahip yazar)
        void SyncInside(Vector3 pos)
        {
            if (!IsSpawned || !IsOwner) return;
            bool inside = FacilityGenerator.IsInteriorPoint(pos);
            if (isInside.Value != inside) isInside.Value = inside;
        }

        void OnColorChanged(int oldValue, int newValue) => PlayerAppearance.ApplyColor(bodyRenderer, newValue);

        // Sahip zıpladı: sayaç artar (herkes ve sunucu görür)
        void OnOwnerJumped()
        {
            if (IsSpawned && IsOwner) jumpCount.Value++;
        }

        // Uzak oyuncuda küçük gövde sıçraması başlat
        void OnJumpChanged(int oldValue, int newValue)
        {
            if (!IsOwner) hopT = 0f;
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
                SyncInside(transform.position);
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
            hopT += Time.deltaTime;
            ApplyBody(shownHeight, PlayerAppearance.HopOffset(hopT));
            if (cameraPivot != null)
            {
                cameraPivot.localPosition = new Vector3(0f, shownHeight - look.eyeOffsetFromTop, 0f);
                cameraPivot.localRotation = Quaternion.Euler(shownPitch, 0f, 0f);
            }
        }

        // 2 m'lik kapsül primitifini verilen boya göre ölçekler
        void ApplyBody(float height, float yOffset = 0f) => PlayerAppearance.ApplyBody(body, height, 0.7f, yOffset);
    }
}
