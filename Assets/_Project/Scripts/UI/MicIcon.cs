using UnityEngine;

namespace MimicGame.UI
{
    /// <summary>Kodla çizilen mikrofon ikonları (asset gerektirmez).</summary>
    public static class MicIcon
    {
        const int Size = 64;
        static Texture2D on, off;

        /// <summary>Açık mikrofon (beyaz).</summary>
        public static Texture2D On => on != null ? on : (on = Build(false));

        /// <summary>Kapalı mikrofon (üstü çizili; renk GUI.color ile verilir).</summary>
        public static Texture2D Off => off != null ? off : (off = Build(true));

        static Texture2D Build(bool slashed)
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
                // y=0 alt satır
                float fx = x + 0.5f, fy = y + 0.5f;
                float a = 0f;

                // Kapsül gövde: merkez x=32, y 30..50, yarıçap 9
                a = Mathf.Max(a, Coverage(DistToSegment(fx, fy, 32f, 32f, 32f, 48f) - 9f));

                // Kavis (U): merkez (32,36), yarıçap 15, kalınlık 3, sadece alt yarı
                float d = Mathf.Abs(Vector2.Distance(new Vector2(fx, fy), new Vector2(32f, 36f)) - 15f) - 1.6f;
                if (fy <= 37f) a = Mathf.Max(a, Coverage(d));

                // Sap ve taban
                a = Mathf.Max(a, Coverage(DistToSegment(fx, fy, 32f, 21f, 32f, 13f) - 1.6f));
                a = Mathf.Max(a, Coverage(DistToSegment(fx, fy, 23f, 12f, 41f, 12f) - 1.8f));

                if (slashed)
                {
                    // Çapraz çizgi, etrafında boşluk bırakılarak
                    float ds = DistToSegment(fx, fy, 12f, 56f, 52f, 8f);
                    a *= 1f - Coverage(ds - 4.5f);
                    a = Mathf.Max(a, Coverage(ds - 2.2f));
                }

                byte alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f);
                px[y * Size + x] = new Color32(255, 255, 255, alpha);
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        // Kenar yumuşatma: mesafe<=-0.5 dolu, >=0.5 boş
        static float Coverage(float signedDist) => Mathf.Clamp01(0.5f - signedDist);

        static float DistToSegment(float px, float py, float ax, float ay, float bx, float by)
        {
            float abx = bx - ax, aby = by - ay;
            float t = ((px - ax) * abx + (py - ay) * aby) / (abx * abx + aby * aby);
            t = Mathf.Clamp01(t);
            float cx = ax + abx * t - px, cy = ay + aby * t - py;
            return Mathf.Sqrt(cx * cx + cy * cy);
        }
    }
}
