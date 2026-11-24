namespace ParolFarm.Core.Math;

using static System.Math;

/// <summary>3D-Vektor (double). Einheiten je nach Kontext: Meter oder Radiant.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => a * s;
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public Vec3 Cross(Vec3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Length => Sqrt(X * X + Y * Y + Z * Z);

    public Vec3 Normalized()
    {
        var l = Length;
        return l < 1e-15 ? Zero : this / l;
    }

    public override string ToString() => $"({X:F4}, {Y:F4}, {Z:F4})";
}

/// <summary>3x3-Rotationsmatrix, zeilenweise gespeichert.</summary>
public readonly struct Rot3
{
    public readonly double M00, M01, M02, M10, M11, M12, M20, M21, M22;

    public Rot3(double m00, double m01, double m02,
                double m10, double m11, double m12,
                double m20, double m21, double m22)
    {
        M00 = m00; M01 = m01; M02 = m02;
        M10 = m10; M11 = m11; M12 = m12;
        M20 = m20; M21 = m21; M22 = m22;
    }

    public static readonly Rot3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public Vec3 ColX => new(M00, M10, M20);
    public Vec3 ColY => new(M01, M11, M21);
    public Vec3 ColZ => new(M02, M12, M22);

    public static Rot3 RotX(double a) { double c = Cos(a), s = Sin(a); return new(1, 0, 0, 0, c, -s, 0, s, c); }
    public static Rot3 RotY(double a) { double c = Cos(a), s = Sin(a); return new(c, 0, s, 0, 1, 0, -s, 0, c); }
    public static Rot3 RotZ(double a) { double c = Cos(a), s = Sin(a); return new(c, -s, 0, s, c, 0, 0, 0, 1); }

    public static Rot3 operator *(Rot3 a, Rot3 b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
        a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
        a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
        a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
        a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
        a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
        a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

    public static Vec3 operator *(Rot3 r, Vec3 v) => new(
        r.M00 * v.X + r.M01 * v.Y + r.M02 * v.Z,
        r.M10 * v.X + r.M11 * v.Y + r.M12 * v.Z,
        r.M20 * v.X + r.M21 * v.Y + r.M22 * v.Z);

    public Rot3 Transposed() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);

    /// <summary>Rotation aus Rotationsvektor (Achse * Winkel, rad) – wie beim UR (Rx, Ry, Rz).</summary>
    public static Rot3 FromRotationVector(Vec3 rv)
    {
        double angle = rv.Length;
        if (angle < 1e-12) return Identity;
        var k = rv / angle;
        double c = Cos(angle), s = Sin(angle), t = 1 - c;
        return new(
            t * k.X * k.X + c, t * k.X * k.Y - s * k.Z, t * k.X * k.Z + s * k.Y,
            t * k.X * k.Y + s * k.Z, t * k.Y * k.Y + c, t * k.Y * k.Z - s * k.X,
            t * k.X * k.Z - s * k.Y, t * k.Y * k.Z + s * k.X, t * k.Z * k.Z + c);
    }

    /// <summary>Rotationsvektor (Achse * Winkel, rad), Winkel in [0, pi].</summary>
    public Vec3 ToRotationVector()
    {
        double cos = System.Math.Clamp((M00 + M11 + M22 - 1) / 2, -1, 1);
        double angle = Acos(cos);
        if (angle < 1e-9) return Vec3.Zero;
        if (PI - angle < 1e-6)
        {
            // Nahe 180°: Achse aus der Diagonale bestimmen
            double xx = Sqrt(System.Math.Max(0, (M00 + 1) / 2));
            double yy = Sqrt(System.Math.Max(0, (M11 + 1) / 2));
            double zz = Sqrt(System.Math.Max(0, (M22 + 1) / 2));
            Vec3 axis;
            if (xx >= yy && xx >= zz) axis = new(xx, (M01 + M10) / (4 * xx), (M02 + M20) / (4 * xx));
            else if (yy >= zz) axis = new((M01 + M10) / (4 * yy), yy, (M12 + M21) / (4 * yy));
            else axis = new((M02 + M20) / (4 * zz), (M12 + M21) / (4 * zz), zz);
            return axis.Normalized() * angle;
        }
        var v = new Vec3(M21 - M12, M02 - M20, M10 - M01);
        return v / (2 * Sin(angle)) * angle;
    }

    /// <summary>Roll-Pitch-Yaw (X-Y-Z fest = Z*Y*X), rad.</summary>
    public static Rot3 FromRpy(double roll, double pitch, double yaw) => RotZ(yaw) * RotY(pitch) * RotX(roll);

    public (double Roll, double Pitch, double Yaw) ToRpy()
    {
        double pitch = Asin(System.Math.Clamp(-M20, -1, 1));
        double roll, yaw;
        if (Abs(Cos(pitch)) > 1e-9)
        {
            roll = Atan2(M21, M22);
            yaw = Atan2(M10, M00);
        }
        else
        {
            roll = 0;
            yaw = Atan2(-M01, M11);
        }
        return (roll, pitch, yaw);
    }

    /// <summary>Orthonormiert die Matrix (gegen numerische Drift).</summary>
    public Rot3 Orthonormalized()
    {
        var x = ColX.Normalized();
        var y = (ColY - x * x.Dot(ColY)).Normalized();
        var z = x.Cross(y);
        return new(x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z);
    }
}

/// <summary>Starre Transformation (Rotation + Translation in Metern).</summary>
public readonly record struct Pose(Rot3 R, Vec3 P)
{
    public static readonly Pose Identity = new(Rot3.Identity, Vec3.Zero);

    public static Pose operator *(Pose a, Pose b) => new(a.R * b.R, a.R * b.P + a.P);

    public Vec3 Transform(Vec3 v) => R * v + P;

    public Pose Inverse()
    {
        var rt = R.Transposed();
        return new(rt, -(rt * P));
    }

    /// <summary>UR-artige Darstellung: X,Y,Z in mm, Rx,Ry,Rz als Rotationsvektor in Grad.</summary>
    public PoseMmDeg ToMmDeg()
    {
        var rv = R.ToRotationVector();
        const double r2d = 180.0 / PI;
        return new(P.X * 1000, P.Y * 1000, P.Z * 1000, rv.X * r2d, rv.Y * r2d, rv.Z * r2d);
    }

    public static Pose FromMmDeg(PoseMmDeg p)
    {
        const double d2r = PI / 180.0;
        return new(Rot3.FromRotationVector(new Vec3(p.Rx * d2r, p.Ry * d2r, p.Rz * d2r)),
                   new Vec3(p.X / 1000, p.Y / 1000, p.Z / 1000));
    }
}

/// <summary>Pose für Anzeige/Speicherung: mm und Rotationsvektor in Grad.</summary>
public record struct PoseMmDeg(double X, double Y, double Z, double Rx, double Ry, double Rz);
