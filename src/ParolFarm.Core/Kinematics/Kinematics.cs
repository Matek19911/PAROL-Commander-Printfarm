namespace ParolFarm.Core.Kinematics;

using ParolFarm.Core.Math;
using static System.Math;

public sealed record IkResult(bool Success, double[] Q, double PositionError, double OrientationError, int Iterations, string? Error);

/// <summary>
/// Vorwärts- und Rückwärtskinematik des PAROL6 inkl. TCP-Offset.
/// Alle Winkel in rad, alle Längen in m.
/// </summary>
public sealed class Parol6Kinematics
{
    /// <summary>TCP relativ zum Flansch (Flansch = Endframe des Original-DH-Modells).</summary>
    public Pose Tool { get; set; } = Pose.Identity;

    static Pose DhTransform(double theta, double d, double a, double alpha)
    {
        double ct = Cos(theta), st = Sin(theta), ca = Cos(alpha), sa = Sin(alpha);
        return new Pose(
            new Rot3(ct, -st * ca, st * sa,
                     st, ct * ca, -ct * sa,
                     0, sa, ca),
            new Vec3(a * ct, a * st, d));
    }

    /// <summary>Flansch-Pose (ohne TCP).</summary>
    public static Pose ForwardFlange(IReadOnlyList<double> q)
    {
        var t = Pose.Identity;
        for (int i = 0; i < Parol6Model.JointCount; i++)
        {
            var (d, a, alpha) = Parol6Model.Dh[i];
            t = t * DhTransform(q[i], d, a, alpha);
        }
        return t;
    }

    /// <summary>TCP-Pose im Basiskoordinatensystem.</summary>
    public Pose Forward(IReadOnlyList<double> q) => ForwardFlange(q) * Tool;

    /// <summary>Geometrische Jacobi-Matrix (6x6) am TCP: Zeilen 0-2 linear, 3-5 rotatorisch.</summary>
    public double[,] Jacobian(IReadOnlyList<double> q)
    {
        var origins = new Vec3[7];
        var axes = new Vec3[7];
        var t = Pose.Identity;
        origins[0] = t.P; axes[0] = t.R.ColZ;
        for (int i = 0; i < 6; i++)
        {
            var (d, a, alpha) = Parol6Model.Dh[i];
            t = t * DhTransform(q[i], d, a, alpha);
            origins[i + 1] = t.P; axes[i + 1] = t.R.ColZ;
        }
        var tcp = (t * Tool).P;
        var j = new double[6, 6];
        for (int i = 0; i < 6; i++)
        {
            var z = axes[i];
            var lin = z.Cross(tcp - origins[i]);
            j[0, i] = lin.X; j[1, i] = lin.Y; j[2, i] = lin.Z;
            j[3, i] = z.X; j[4, i] = z.Y; j[5, i] = z.Z;
        }
        return j;
    }

    /// <summary>Fehler zwischen Ziel und Ist: [dp; dω] im Basissystem.</summary>
    public static double[] PoseError(Pose target, Pose current)
    {
        var dp = target.P - current.P;
        var dw = (target.R * current.R.Transposed()).ToRotationVector();
        return new[] { dp.X, dp.Y, dp.Z, dw.X, dw.Y, dw.Z };
    }

    /// <summary>
    /// Numerische IK (gedämpfte kleinste Quadrate), startet bei <paramref name="seed"/>
    /// und bleibt dadurch in derselben Konfiguration (kein Umklappen).
    /// </summary>
    public IkResult Inverse(Pose target, IReadOnlyList<double> seed, int maxIterations = 100,
                            double posTol = 1e-5, double rotTol = 1e-4)
    {
        // Levenberg-Marquardt mit adaptiver Dämpfung: robust auch nahe Singularitäten
        const double w = 0.1; // Orientierung gewichten (1 rad ~ 0.1 m)
        var q = seed.ToArray();
        double lambda = 1e-3;
        var e = PoseError(target, Forward(q));
        double cost = WeightedNorm(e, w);
        int it = 0;
        for (; it < maxIterations; it++)
        {
            if (Converged(e, posTol, rotTol)) return Finish(q, e, it);

            var ew = new[] { e[0], e[1], e[2], e[3] * w, e[4] * w, e[5] * w };
            var j = Jacobian(q);
            for (int c = 0; c < 6; c++) { j[3, c] *= w; j[4, c] *= w; j[5, c] *= w; }

            var dq = LinAlg.DampedLeastSquares(j, ew, lambda);
            if (dq == null) { lambda *= 10; continue; }

            // Schrittweite begrenzen (max. 0.2 rad je Iteration)
            double maxStep = dq.Max(Abs);
            double scale = maxStep > 0.2 ? 0.2 / maxStep : 1;
            var qn = new double[6];
            for (int i = 0; i < 6; i++)
                qn[i] = System.Math.Clamp(q[i] + dq[i] * scale, Parol6Model.MinRad(i), Parol6Model.MaxRad(i));

            var en = PoseError(target, Forward(qn));
            double cn = WeightedNorm(en, w);
            if (cn < cost)
            {
                q = qn; e = en; cost = cn;
                lambda = System.Math.Max(lambda / 3, 1e-7);
            }
            else
            {
                lambda *= 5;
                if (lambda > 10) break;
            }
        }
        if (Converged(e, posTol, rotTol)) return Finish(q, e, it);
        var (ep, er) = Norms(e);
        return new IkResult(false, q, ep, er, it,
            $"Pose nicht erreichbar (Rest: {ep * 1000:F2} mm / {er * 180 / PI:F2}°)");
    }

    static (double Pos, double Rot) Norms(double[] e) =>
        (Sqrt(e[0] * e[0] + e[1] * e[1] + e[2] * e[2]), Sqrt(e[3] * e[3] + e[4] * e[4] + e[5] * e[5]));

    static double WeightedNorm(double[] e, double w) =>
        e[0] * e[0] + e[1] * e[1] + e[2] * e[2] + w * w * (e[3] * e[3] + e[4] * e[4] + e[5] * e[5]);

    static bool Converged(double[] e, double posTol, double rotTol)
    {
        var (ep, er) = Norms(e);
        return ep < posTol && er < rotTol;
    }

    static IkResult Finish(double[] q, double[] e, int it)
    {
        var (ep, er) = Norms(e);
        if (!Parol6Model.WithinLimits(q, -1e-9))
            return new IkResult(false, q, ep, er, it, "Gelenkgrenze");
        return new IkResult(true, q, ep, er, it, null);
    }
}
