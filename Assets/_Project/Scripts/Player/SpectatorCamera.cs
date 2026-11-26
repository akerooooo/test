using MimicGame.Items;
using MimicGame.Mimic;
using MimicGame.Network;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MimicGame.Player
{
    /// <summary>
    /// Yerel ölü oyuncunun gözlemci kamerası (kameranın üstünde, varsayılan kapalı; PlayerHealth açar).
    /// Hayattaki bir oyuncunun başı etrafında üçüncü şahıs yörüngesi; sol tık / Boşluk sonrakine geçer.
    /// Kimse hayatta değilse ölüm noktası etrafında yavaşça döner. AudioListener kamerada kalır.
    /// </summary>
    public class SpectatorCamera : MonoBehaviour
    {
        public float distance = 3f;
        public float sensitivity = 0.08f;
        public float collisionRadius = 0.25f;

        /// <summary>İzlenen oyuncunun clientId'si (HUD için); izlenen yoksa HasTarget false.</summary>
        public ulong WatchedClientId { get; private set; }
        public bool HasTarget { get; private set; }

        PlayerHealth self;
        PlayerLook look;
        PlayerHealth target;
        float yaw, pitch = 20f;
        Vector3 deathPos;
        Vector3 focus;
        bool hasFocus;
        static readonly RaycastHit[] hits = new RaycastHit[16];

        void Awake()
        {
            self = GetComponentInParent<PlayerHealth>();
            look = GetComponentInParent<PlayerLook>();
        }

        void OnEnable()
        {
            if (self == null) self = GetComponentInParent<PlayerHealth>();
            deathPos = (self != null ? self.transform.position : transform.position) + Vector3.up * 1.5f;
            yaw = transform.eulerAngles.y;
            hasFocus = false;
            target = null;
            PickTarget(0);
        }

        void OnDisable()
        {
            HasTarget = false;
            target = null;
            // Kamera yerel konumuna döner; konumu PlayerLook.LateUpdate yeniden kurar
            transform.localRotation = Quaternion.Euler(look != null ? look.Pitch : 0f, 0f, 0f);
        }

        // dir: +1 sonraki canlı oyuncu; 0 geçerli kalır, geçersizse ilkini seçer
        void PickTarget(int dir)
        {
            var all = PlayerHealth.All;
            int n = all.Count;
            if (n == 0) { target = null; HasTarget = false; return; }

            int start = target != null ? all.IndexOf(target) : -1;
            for (int k = 1; k <= n; k++)
            {
                int idx = ((start + (dir == 0 && start >= 0 ? k - 1 : k)) % n + n) % n;
                var h = all[idx];
                if (h == null || !h.IsSpawned || h.Dead || h == self) continue;
                if (dir == 0 && target != null && h == target) return;
                target = h;
                WatchedClientId = h.OwnerClientId;
                HasTarget = true;
                return;
            }
            target = null;
            HasTarget = false;
        }

        void LateUpdate()
        {
            // İzlenen ölürse/ayrılırsa sıradakine geç
            if (target != null && (!target.IsSpawned || target.Dead)) PickTarget(1);
            if (target == null && PlayerHealth.AliveCount > 0) PickTarget(1);

            var mouse = Mouse.current;
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) PlayerLook.SetCursorLocked(false);

            bool locked = Cursor.lockState == CursorLockMode.Locked;
            if (!locked)
            {
                // İmleç serbestken tıklama yalnızca kilitler (PlayerLook ile aynı davranış)
                if (mouse != null && mouse.leftButton.wasPressedThisFrame &&
                    !NetworkMenu.PointerOverButton(mouse.position.ReadValue()))
                    PlayerLook.SetCursorLocked(true);
            }
            else
            {
                if (mouse != null)
                {
                    Vector2 d = mouse.delta.ReadValue() * sensitivity;
                    yaw += d.x;
                    pitch = Mathf.Clamp(pitch - d.y, -60f, 75f);
                }
                bool next = (mouse != null && mouse.leftButton.wasPressedThisFrame) ||
                            (kb != null && kb.spaceKey.wasPressedThisFrame);
                if (next) PickTarget(1);
            }

            Vector3 wantFocus;
            if (target != null)
            {
                var pn = target.GetComponent<PlayerNetwork>();
                wantFocus = pn != null && pn.cameraPivot != null
                    ? pn.cameraPivot.position
                    : target.transform.position + Vector3.up * 1.6f;
            }
            else
            {
                wantFocus = deathPos;
                yaw += 12f * Time.deltaTime; // kimse hayatta değil: yavaş yörünge
            }
            focus = hasFocus ? Vector3.Lerp(focus, wantFocus, 1f - Mathf.Exp(-12f * Time.deltaTime)) : wantFocus;
            hasFocus = true;

            var rot = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 back = rot * Vector3.back;
            float dist = ClampDistance(focus, back, distance);
            transform.SetPositionAndRotation(focus + back * dist, rot);
        }

        // Duvara çarpıyorsa kamerayı öne çeker; oyuncu kapsülü, eşya ve taklitçi yok sayılır
        float ClampDistance(Vector3 origin, Vector3 dir, float max)
        {
            int n = Physics.SphereCastNonAlloc(origin, collisionRadius, dir, hits, max, ~0,
                QueryTriggerInteraction.Ignore);
            float best = max;
            for (int i = 0; i < n; i++)
            {
                var col = hits[i].collider;
                if (col == null || col is CharacterController) continue;
                if (col.GetComponentInParent<Item>() != null) continue;
                if (col.GetComponentInParent<MimicAI>() != null) continue;
                if (hits[i].distance < best) best = hits[i].distance;
            }
            return Mathf.Max(0.3f, best);
        }
    }
}
