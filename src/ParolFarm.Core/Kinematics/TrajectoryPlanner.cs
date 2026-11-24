namespace ParolFarm.Core.Kinematics;

using ParolFarm.Core.Math;
using static System.Math;

/// <summary>Vorab berechnete Bahn: ein Gelenkvektor pro Regeltakt.</summary>
public sealed record Trajectory(IReadOnlyList<double[]> Points, double Dt)
{
    public double Duration => Points.Count * Dt;
}

public sealed record PlanResult(Trajectory? Trajectory, string? Error)
{
    public bool Success => Trajectory != null;
    public static PlanResult Fail(string e) => new(null, e);
}

/// <summary>
/// Plant MoveJ (Gelenkraum) und MoveL (kartesisch linear) mit Trapezprofil.
/// Die komplette Bahn wird vor dem Start berechnet und auf Erreichbarkeit,
/// Gelenkgrenzen und Gelenkgeschwindigkeit geprüft.
/// </summary>
public sealed class TrajectoryPlanner
{
    readonly Parol6Kinematics _kin;
    public double Dt { get; }

    /// <summary>Zeit bis zur vollen Geschwindigkeit (s).</summary>
    public double RampTime { get; set; } = 0.4;

    public TrajectoryPlanner(Parol6Kinematics kin, double dt = 0.01)
    {
        _kin = kin;
        Dt = dt;
    }

    /// <summary>Normiertes Trapezprofil s(t) von 0 bis 1.</summary>
    /// <param name="vmax">Max. Geschwindigkeit in 1/s (normiert)</param>
    /// <param name="amax">Max. Beschleunigung in 1/s² (normiert)</param>
    public static List<double> TrapezoidProfile(double vmax, double amax, double dt)
    {
        var s = new List<double>();
        double tAcc = vmax / amax;
        double dAcc = 0.5 * amax * tAcc * tAcc;
        double tConst;
        if (2 * dAcc >= 1)
        {
            // Dreieckprofil
            tAcc = Sqrt(1 / amax);
            vmax = amax * tAcc;
            tConst = 0;
            dAcc = 0.5;
        }
        else tConst = (1 - 2 * dAcc) / vmax;
        double total = 2 * tAcc + tConst;
        int n = System.Math.Max(1, (int)Ceiling(total / dt));
        for (int i = 1; i <= n; i++)
        {
            double t = System.Math.Min(i * dt, total);
            double v;
            if (t < tAcc) v = 0.5 * amax * t * t;
            else if (t < tAcc + tConst) v = dAcc + vmax * (t - tAcc);
            else
            {
                double td = total - t;
                v = 1 - 0.5 * amax * td * td;
            }
            s.Add(System.Math.Clamp(v, 0, 1));
        }
        s[^1] = 1;
        return s;
    }

    /// <summary>Gelenkfahrt. speedFraction 0..1 bezogen auf die maximale Gelenkgeschwindigkeit.</summary>
    public PlanResult PlanJoint(IReadOnlyList<double> from, IReadOnlyList<double> to, double speedFraction)
    {
        if (!Parol6Model.WithinLimits(to, -1e-9)) return PlanResult.Fail("Zielposition außerhalb der Gelenkgrenzen");
        speedFraction = System.Math.Clamp(speedFraction, 0.01, 1);
        double vS = double.MaxValue;
        bool any = false;
        for (int j = 0; j < 6; j++)
        {
            double d = Abs(to[j] - from[j]);
            if (d < 1e-9) continue;
            any = true;
            vS = System.Math.Min(vS, Parol6Model.MaxSpeedRad(j) * speedFraction / d);
        }
        if (!any) return new PlanResult(new Trajectory(new[] { to.ToArray() }, Dt), null);
        double aS = vS / RampTime;
        var prof = TrapezoidProfile(vS, aS, Dt);
        var pts = new List<double[]>(prof.Count);
        foreach (var s in prof)
        {
            var q = new double[6];
            for (int j = 0; j < 6; j++) q[j] = from[j] + (to[j] - from[j]) * s;
            pts.Add(q);
        }
        return new PlanResult(new Trajectory(pts, Dt), null);
    }

    /// <summary>Lineare kartesische Fahrt des TCP.</summary>
    /// <param name="linSpeed">m/s</param>
    /// <param name="angSpeed">rad/s</param>
    public PlanResult PlanLinear(IReadOnlyList<double> from, Pose target, double linSpeed, double angSpeed)
    {
        var start = _kin.Forward(from);
        double dist = (target.P - start.P).Length;
        var relRot = (start.R.Transposed() * target.R).ToRotationVector(); // im Werkzeugsystem des Starts
        double angle = relRot.Length;
        if (dist < 1e-7 && angle < 1e-6)
            return new PlanResult(new Trajectory(new[] { from.ToArray() }, Dt), null);

        double vS = double.MaxValue;
        if (dist > 1e-7) vS = System.Math.Min(vS, linSpeed / dist);
        if (angle > 1e-6) vS = System.Math.Min(vS, angSpeed / angle);
        double aS = vS / RampTime;
        var prof = TrapezoidProfile(vS, aS, Dt);

        var pts = new List<double[]>(prof.Count);
        var q = from.ToArray();
        for (int i = 0; i < prof.Count; i++)
        {
            double s = prof[i];
            var p = start.P + (target.P - start.P) * s;
            var r = start.R * Rot3.FromRotationVector(relRot * s);
            var ik = _kin.Inverse(new Pose(r, p), q, 50);
            if (!ik.Success)
                return PlanResult.Fail($"Lineare Bahn bei {s * 100:F0}% nicht fahrbar: {ik.Error}");
            for (int j = 0; j < 6; j++)
            {
                double vel = Abs(ik.Q[j] - q[j]) / Dt;
                if (vel > Parol6Model.MaxSpeedRad(j))
                    return PlanResult.Fail(
                        $"Lineare Bahn bei {s * 100:F0}%: Gelenk {j + 1} zu schnell (Singularität?) – langsamer fahren oder MoveJ verwenden");
            }
            q = ik.Q;
            pts.Add(q);
        }
        return new PlanResult(new Trajectory(pts, Dt), null);
    }
}
