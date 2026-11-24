namespace ParolFarm.Core.Math;

/// <summary>Kleine dichte Lineare Algebra für 6x6-Probleme (keine externen Abhängigkeiten).</summary>
public static class LinAlg
{
    /// <summary>Löst A x = b per Gauß-Elimination mit Pivotsuche. A und b werden nicht verändert.</summary>
    public static double[]? Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            double best = System.Math.Abs(m[col, col]);
            for (int r = col + 1; r < n; r++)
            {
                double v = System.Math.Abs(m[r, col]);
                if (v > best) { best = v; piv = r; }
            }
            if (best < 1e-14) return null;
            if (piv != col)
            {
                for (int c = 0; c < n; c++) (m[col, c], m[piv, c]) = (m[piv, c], m[col, c]);
                (x[col], x[piv]) = (x[piv], x[col]);
            }
            for (int r = col + 1; r < n; r++)
            {
                double f = m[r, col] / m[col, col];
                if (f == 0) continue;
                for (int c = col; c < n; c++) m[r, c] -= f * m[col, c];
                x[r] -= f * x[col];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double s = x[r];
            for (int c = r + 1; c < n; c++) s -= m[r, c] * x[c];
            x[r] = s / m[r, r];
        }
        return x;
    }

    /// <summary>Gedämpfte kleinste Quadrate: dq = J^T (J J^T + λ² I)^-1 e.</summary>
    public static double[]? DampedLeastSquares(double[,] j, double[] e, double lambda)
    {
        int rows = j.GetLength(0), cols = j.GetLength(1);
        var jjt = new double[rows, rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < rows; c++)
            {
                double s = 0;
                for (int k = 0; k < cols; k++) s += j[r, k] * j[c, k];
                jjt[r, c] = s + (r == c ? lambda * lambda : 0);
            }
        var y = Solve(jjt, e);
        if (y == null) return null;
        var dq = new double[cols];
        for (int c = 0; c < cols; c++)
        {
            double s = 0;
            for (int r = 0; r < rows; r++) s += j[r, c] * y[r];
            dq[c] = s;
        }
        return dq;
    }

    /// <summary>Manipulierbarkeit sqrt(det(J J^T)) – nahe 0 bedeutet Singularität.</summary>
    public static double Manipulability(double[,] j)
    {
        int rows = j.GetLength(0), cols = j.GetLength(1);
        var m = new double[rows, rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < rows; c++)
            {
                double s = 0;
                for (int k = 0; k < cols; k++) s += j[r, k] * j[c, k];
                m[r, c] = s;
            }
        double det = Determinant(m);
        return System.Math.Sqrt(System.Math.Max(0, det));
    }

    public static double Determinant(double[,] a)
    {
        int n = a.GetLength(0);
        var m = (double[,])a.Clone();
        double det = 1;
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (System.Math.Abs(m[r, col]) > System.Math.Abs(m[piv, col])) piv = r;
            if (System.Math.Abs(m[piv, col]) < 1e-300) return 0;
            if (piv != col)
            {
                for (int c = 0; c < n; c++) (m[col, c], m[piv, c]) = (m[piv, c], m[col, c]);
                det = -det;
            }
            det *= m[col, col];
            for (int r = col + 1; r < n; r++)
            {
                double f = m[r, col] / m[col, col];
                for (int c = col; c < n; c++) m[r, c] -= f * m[col, c];
            }
        }
        return det;
    }
}
