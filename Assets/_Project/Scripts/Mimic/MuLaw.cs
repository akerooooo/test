namespace MimicGame.Mimic
{
    /// <summary>G.711 µ-law 8 bit kodlayıcı/çözücü.</summary>
    public static class MuLaw
    {
        const int Bias = 0x84;
        const int Clip = 32635;

        public static byte Encode(float sample)
        {
            int s = (int)(Clamp(sample) * 32767f);
            int sign = 0;
            if (s < 0) { s = -s; sign = 0x80; }
            if (s > Clip) s = Clip;
            s += Bias;
            int exp = 7;
            for (int mask = 0x4000; (s & mask) == 0 && exp > 0; exp--, mask >>= 1) { }
            int mant = (s >> (exp + 3)) & 0x0F;
            return (byte)~(sign | (exp << 4) | mant);
        }

        public static float Decode(byte b)
        {
            int u = ~b & 0xFF;
            int sign = u & 0x80;
            int exp = (u >> 4) & 0x07;
            int mant = u & 0x0F;
            int s = (((mant << 3) + Bias) << exp) - Bias;
            if (sign != 0) s = -s;
            return s / 32768f;
        }

        static float Clamp(float v) => v < -1f ? -1f : (v > 1f ? 1f : v);
    }
}
