namespace ParolFarm.Core.Kinematics;

using static System.Math;

/// <summary>
/// Geometrie- und Antriebsdaten des PAROL6.
/// Werte übernommen aus PAROL6_ROBOT.py der originalen PAROL6-Commander-Software (GPL-3.0),
/// damit Gelenkwinkel und Positionen 1:1 zum Original passen.
/// </summary>
public static class Parol6Model
{
    public const int JointCount = 6;

    // Längen in Metern
    const double A1 = 110.50 / 1000, A2 = 23.42 / 1000, A3 = 180.0 / 1000, A4 = 43.5 / 1000,
                 A5 = 176.35 / 1000, A6 = 62.8 / 1000, A7 = 45.25 / 1000;

    /// <summary>Standard-DH-Parameter (d, a, alpha) je Gelenk, theta = Gelenkwinkel.</summary>
    public static readonly (double D, double A, double Alpha)[] Dh =
    {
        (A1,  A2,  -PI / 2),
        (0,   A3,   PI),
        (0,  -A4,   PI / 2),
        (-A5, 0,   -PI / 2),
        (0,   0,    PI / 2),
        (-A6, -A7,  PI),
    };

    /// <summary>Gelenkgrenzen in Grad (nach Referenzfahrt).</summary>
    public static readonly (double Min, double Max)[] JointLimitsDeg =
    {
        (-123.046875, 123.046875),
        (-145.0088, -3.375),
        (107.866, 287.8675),
        (-105.46975, 105.46975),
        (-90, 90),
        (0, 360),
    };

    /// <summary>Standby-Position in Grad (nach Referenzfahrt).</summary>
    public static readonly double[] StandbyDeg = { 0, -90, 180, 0, 0, 180 };

    /// <summary>Getriebeuntersetzung je Gelenk.</summary>
    public static readonly double[] ReductionRatio = { 6.4, 20, 20 * (38.0 / 42.0), 4, 4, 10 };

    /// <summary>Maximale Motorgeschwindigkeit in Schritten/s (aus dem Original).</summary>
    public static readonly double[] MaxSpeedSteps = { 6500, 18000, 20000, 22000, 22000, 22000 };

    const int Microstep = 32;
    const int StepsPerRev = 200;
    public const double DegPerStep = 360.0 / (Microstep * StepsPerRev);

    public static double DegToSteps(double deg, int joint) => deg / DegPerStep * ReductionRatio[joint];
    public static double StepsToDeg(double steps, int joint) => steps * DegPerStep / ReductionRatio[joint];
    public static double RadToSteps(double rad, int joint) => DegToSteps(rad * 180 / PI, joint);
    public static double StepsToRad(double steps, int joint) => StepsToDeg(steps, joint) * PI / 180;

    public static double MinRad(int j) => JointLimitsDeg[j].Min * PI / 180;
    public static double MaxRad(int j) => JointLimitsDeg[j].Max * PI / 180;

    /// <summary>Maximale Gelenkgeschwindigkeit in rad/s.</summary>
    public static double MaxSpeedRad(int j) => StepsToRad(MaxSpeedSteps[j], j);

    public static double[] StandbyRad => StandbyDeg.Select(d => d * PI / 180).ToArray();

    public static bool WithinLimits(IReadOnlyList<double> q, double marginRad = 0)
    {
        for (int j = 0; j < JointCount; j++)
            if (q[j] < MinRad(j) + marginRad || q[j] > MaxRad(j) - marginRad) return false;
        return true;
    }
}
