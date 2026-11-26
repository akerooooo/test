using UnityEngine;

namespace MimicGame.Mimic
{
    /// <summary>Oyuncu ve taklitçinin ortak görünümü: renk paleti ve gövde ölçeği.</summary>
    public static class PlayerAppearance
    {
        public static readonly Color[] Palette =
        {
            new Color(0.25f, 0.40f, 0.60f), // mavi
            new Color(0.60f, 0.30f, 0.28f), // kırmızı
            new Color(0.30f, 0.50f, 0.32f), // yeşil
            new Color(0.62f, 0.52f, 0.25f), // hardal
            new Color(0.45f, 0.32f, 0.55f), // mor
            new Color(0.28f, 0.52f, 0.52f), // turkuaz
        };

        static MaterialPropertyBlock block;

        public static Color GetColor(int index)
        {
            int n = Palette.Length;
            return Palette[((index % n) + n) % n];
        }

        public static void ApplyColor(Renderer r, int index)
        {
            if (r == null) return;
            if (block == null) block = new MaterialPropertyBlock();
            var c = GetColor(index);
            r.GetPropertyBlock(block);
            block.SetColor("_BaseColor", c); // URP Lit
            block.SetColor("_Color", c);     // yedek
            r.SetPropertyBlock(block);
        }

        // 2 m'lik kapsül primitifini verilen boya göre ölçekler
        public static void ApplyBody(Transform body, float height)
        {
            if (body == null) return;
            body.localScale = new Vector3(0.7f, height * 0.5f, 0.7f);
            body.localPosition = new Vector3(0f, height * 0.5f, 0f);
        }
    }
}
