using MimicGame.Environment;
using UnityEngine;

namespace MimicGame.Level
{
    /// <summary>Dış cephe kapısı ile iç mekân çıkış kapısının ortak ölçüleri.</summary>
    public static class FacilityDoorSpec
    {
        public const float Width = 2.4f;   // açıklık genişliği
        public const float Height = 3.0f;  // açıklık yüksekliği
        public const float FramePost = 0.15f;
        public const float FrameDepth = 0.3f;
        public const float PanelThickness = 0.1f;
        public const float LampY = 3.26f;  // "çıkış" lambasının merkez yüksekliği
    }

    /// <summary>Kapı malzemeleri (iki kapı da aynı malzemeleri kullanır).</summary>
    public sealed class FacilityDoorMaterials
    {
        public Material panel, frame, metal, lamp;
    }

    /// <summary>
    /// Çift kanatlı metal kapıyı (panel, çerçeve, kollar, çıkış lambası) ilkel şekillerle kurar.
    /// Hem TestSceneBuilder (cephe) hem FacilityGenerator (iç mekân) bunu kullanır; böylece boyut ve görünüm aynıdır.
    /// Yerel +Z = kapının önü (kullanıcı tarafı).
    /// </summary>
    public static class FacilityDoorKit
    {
        public static FacilityDoor Build(Transform parent, string name, Vector3 localPos, float yaw,
            FacilityDoorMaterials m, bool isExit, float lampZ)
        {
            var root = new GameObject(name);
            root.SetActive(false); // FacilityDoor.OnEnable doğru isExit ile çalışsın
            var t = root.transform;
            t.SetParent(parent, false);
            t.localPosition = localPos;
            t.localRotation = Quaternion.Euler(0f, yaw, 0f);

            float W = FacilityDoorSpec.Width, H = FacilityDoorSpec.Height;
            float post = FacilityDoorSpec.FramePost, depth = FacilityDoorSpec.FrameDepth;

            // Kanatlar (tek çarpıştırıcı) ve ayrıntılar
            Box(t, "Kapı Paneli", new Vector3(0f, H * 0.5f, 0f), new Vector3(W, H, FacilityDoorSpec.PanelThickness), m.panel, true);
            Box(t, "Orta Çizgi", new Vector3(0f, H * 0.5f, 0f), new Vector3(0.04f, H - 0.1f, 0.12f), m.frame, false);
            Box(t, "Alt Plaka Sol", new Vector3(-0.6f, 0.2f, 0f), new Vector3(1.0f, 0.36f, 0.12f), m.metal, false);
            Box(t, "Alt Plaka Sağ", new Vector3(0.6f, 0.2f, 0f), new Vector3(1.0f, 0.36f, 0.12f), m.metal, false);
            for (int s = -1; s <= 1; s += 2)
            {
                Box(t, "Kol Ön", new Vector3(0.2f * s, 1.1f, 0.09f), new Vector3(0.05f, 0.5f, 0.05f), m.metal, false);
                Box(t, "Kol Arka", new Vector3(0.2f * s, 1.1f, -0.09f), new Vector3(0.05f, 0.5f, 0.05f), m.metal, false);
            }

            // Çerçeve: iç yüzler 1.5 cm içeride (duvar yüzeyiyle z-fight olmasın)
            float postH = H - 0.015f;
            float px = W * 0.5f + post * 0.4f;
            Box(t, "Çerçeve Sol", new Vector3(-px, postH * 0.5f, 0f), new Vector3(post, postH, depth), m.frame, false);
            Box(t, "Çerçeve Sağ", new Vector3(px, postH * 0.5f, 0f), new Vector3(post, postH, depth), m.frame, false);
            Box(t, "Çerçeve Üst", new Vector3(0f, postH + post * 0.5f, 0f), new Vector3(W + post * 1.8f, post, depth), m.frame, false);

            // Üstte aynı çıkış lambası
            Box(t, "Lamba Kasası", new Vector3(0f, FacilityDoorSpec.LampY, lampZ), new Vector3(0.7f, 0.18f, 0.2f), m.metal, false);
            Box(t, "Lamba Camı", new Vector3(0f, FacilityDoorSpec.LampY, lampZ + 0.105f), new Vector3(0.55f, 0.1f, 0.01f), m.lamp, false);

            var door = root.AddComponent<FacilityDoor>();
            door.isExit = isExit;
            root.SetActive(true);
            return door;
        }

        static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 size, Material mat, bool collider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!collider)
            {
                var col = go.GetComponent<Collider>();
                if (col != null) Object.DestroyImmediate(col);
            }
            return go;
        }
    }
}
