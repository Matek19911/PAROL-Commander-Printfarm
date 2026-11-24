namespace ParolFarm.Core.Settings;

using ParolFarm.Core.Math;

/// <summary>Zustand der 6 Achsen + Tasten einer 3Dconnexion SpaceMouse (Rohwerte).</summary>
public sealed class SpaceMouseState
{
    public int Tx, Ty, Tz, Rx, Ry, Rz;
    public uint Buttons;

    /// <summary>
    /// Wertet einen HID-Report aus. Unterstützt beide Formate:
    /// ältere Geräte (Report 1 = Translation, Report 2 = Rotation) und neuere
    /// (Report 1 mit allen 6 Achsen). Report 3 = Tasten.
    /// </summary>
    /// <returns>true, wenn sich etwas geändert hat.</returns>
    public bool ApplyReport(ReadOnlySpan<byte> r)
    {
        if (r.Length < 2) return false;
        static int S16(ReadOnlySpan<byte> b, int i) => (short)(b[i] | (b[i + 1] << 8));
        switch (r[0])
        {
            case 1 when r.Length >= 7:
                Tx = S16(r, 1); Ty = S16(r, 3); Tz = S16(r, 5);
                if (r.Length >= 13) { Rx = S16(r, 7); Ry = S16(r, 9); Rz = S16(r, 11); }
                return true;
            case 2 when r.Length >= 7:
                Rx = S16(r, 1); Ry = S16(r, 3); Rz = S16(r, 5);
                return true;
            case 3:
                uint b = 0;
                for (int i = 1; i < System.Math.Min(r.Length, 5); i++) b |= (uint)r[i] << (8 * (i - 1));
                Buttons = b;
                return true;
        }
        return false;
    }
}

/// <summary>Rechnet SpaceMouse-Rohwerte in normierte Jog-Vorgaben (-1..1) um.</summary>
public static class SpaceMouseMapper
{
    public static (Vec3 Lin, Vec3 Ang) Map(SpaceMouseState s, SpaceMouseSettings cfg)
    {
        double Shape(int raw)
        {
            double v = System.Math.Clamp(raw / cfg.FullScale, -1, 1);
            double a = System.Math.Abs(v);
            if (a < cfg.Deadband) return 0;
            double n = (a - cfg.Deadband) / (1 - cfg.Deadband);
            return System.Math.Sign(v) * System.Math.Pow(n, cfg.Exponent);
        }

        var t = new[] { Shape(s.Tx) * cfg.TranslationSign[0], Shape(s.Ty) * cfg.TranslationSign[1], Shape(s.Tz) * cfg.TranslationSign[2] };
        var r = new[] { Shape(s.Rx) * cfg.RotationSign[0], Shape(s.Ry) * cfg.RotationSign[1], Shape(s.Rz) * cfg.RotationSign[2] };
        if (!cfg.TranslationEnabled) Array.Clear(t);
        if (!cfg.RotationEnabled) Array.Clear(r);

        if (cfg.DominantAxisOnly)
        {
            int best = -1; double bv = 0;
            for (int i = 0; i < 6; i++)
            {
                double v = System.Math.Abs(i < 3 ? t[i] : r[i - 3]);
                if (v > bv) { bv = v; best = i; }
            }
            for (int i = 0; i < 3; i++)
            {
                if (i != best) t[i] = 0;
                if (i + 3 != best) r[i] = 0;
            }
        }
        return (new Vec3(t[0], t[1], t[2]), new Vec3(r[0], r[1], r[2]));
    }
}
