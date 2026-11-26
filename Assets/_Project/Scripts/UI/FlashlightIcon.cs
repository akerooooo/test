using UnityEngine;

namespace MimicGame.UI
{
    /// <summary>Kodla çizilen el feneri ikonu (asset gerektirmez; renk GUI.color ile verilir).</summary>
    public static class FlashlightIcon
    {
        const int Size = 64;
        static Texture2D icon;

        public static Texture2D Icon => icon != null ? icon : (icon = Build());

        static Texture2D Build()
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f;
                float a = 0f;
                // Fener yatay: sağda baş (huni), solda gövde. y=0 alt satır
                a = Mathf.Max(a, Coverage(DistToSegment(fx, fy, 10f, 32f, 38f, 32f) - 6f)); // gövde
                a = Mathf.Max(a, Coverage(DistToSegment(fx, fy, 40f, 32f, 46f, 32f) - 10f)); // baş
                // Huni ağzı
                a = Mathf.Max(a, Coverage(DistToSegment(fx, fy, 48f, 32f, 48f, 32f) - 11f));
                // Işın çizgileri
                a = Mathf.Max(a, Coverage(DistToSegment(fx, fy, 54f, 32f, 62f, 32f) - 1.3f));
                a = Mathf.Max(a, Coverage(DistToSegment(fx, fy, 53f, 40f, 60f, 46f) - 1.3f));
                a = Mathf.Max(a, Coverage(DistToSegment(fx, fy, 53f, 24f, 60f, 18f) - 1.3f));
                // Gövde üzerindeki düğme boşluğu
                a *= 1f - Coverage(DistToSegment(fx, fy, 22f, 38f, 26f, 38f) - 1.6f);
                px[y * Size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        static float Coverage(float signedDist) => Mathf.Clamp01(0.5f - signedDist);

        static float DistToSegment(float px, float py, float ax, float ay, float bx, float by)
        {
            float abx = bx - ax, aby = by - ay;
            float len2 = abx * abx + aby * aby;
            float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(((px - ax) * abx + (py - ay) * aby) / len2);
            float cx = ax + abx * t - px, cy = ay + aby * t - py;
            return Mathf.Sqrt(cx * cx + cy * cy);
        }
    }
}
