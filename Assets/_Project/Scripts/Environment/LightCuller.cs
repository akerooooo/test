using System.Collections.Generic;
using UnityEngine;

namespace MimicGame.Environment
{
    /// <summary>
    /// Kayıtlı ışıkları yerel kameradan 30 m'den uzaktaysa kapatır (0.5 sn'de bir). Performans içindir.
    /// Işık nesnesi bütünüyle kapanır (BlinkLight dahil).
    /// </summary>
    public class LightCuller : MonoBehaviour
    {
        public float distance = 30f;
        public float interval = 0.5f;
        [Tooltip("Farklı katlardaki ışıklar (dikey fark bunu aşarsa) kapanır.")]
        public float verticalRange = 5f;

        readonly List<Light> lights = new List<Light>(96);
        float next;

        public int Count => lights.Count;

        public void Register(Light l)
        {
            if (l != null) lights.Add(l);
        }

        void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + interval;
            var cam = FindCamera();
            if (cam == null) return;
            Vector3 p = cam.transform.position;
            float sq = distance * distance;
            for (int i = lights.Count - 1; i >= 0; i--)
            {
                var l = lights[i];
                if (l == null) { lights.RemoveAt(i); continue; }
                Vector3 dv = l.transform.position - p;
                bool near = dv.sqrMagnitude <= sq && Mathf.Abs(dv.y) <= verticalRange;
                var go = l.gameObject;
                if (go.activeSelf != near) go.SetActive(near);
            }
        }

        static Camera FindCamera()
        {
            var main = Camera.main;
            if (main != null && main.enabled) return main;
            var all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].enabled && all[i].targetTexture == null) return all[i];
            return null;
        }
    }
}
