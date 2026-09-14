namespace Nova4Me2.Core.Forensics;

public static class Entropy
{
    /// <summary>Shannon entropy in bits per byte (0 = constant, 8 = uniformly random).</summary>
    public static double Shannon(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        Span<int> counts = stackalloc int[256];
        foreach (var b in data) counts[b]++;
        double h = 0, n = data.Length;
        for (int i = 0; i < 256; i++)
        {
            if (counts[i] == 0) continue;
            double p = counts[i] / n;
            h -= p * Math.Log2(p);
        }
        return h;
    }

    /// <summary>Entropy per block, for profiling a file (compressed/encrypted tails stand out).</summary>
    public static List<double> Profile(ICarveSource src, long offset, long length, int block = 4096, int maxBlocks = 4096)
    {
        var list = new List<double>();
        long step = Math.Max(block, (length + maxBlocks - 1) / maxBlocks);
        var buf = new byte[block];
        for (long pos = offset; pos < offset + length; pos += step)
        {
            int n = (int)Math.Min(block, offset + length - pos);
            src.Read(pos, buf.AsSpan(0, n));
            list.Add(Shannon(buf.AsSpan(0, n)));
        }
        return list;
    }

    /// <summary>Regularised lower incomplete gamma P(a, x) (series / continued fraction), for chi-square p-values.</summary>
    public static double GammaP(double a, double x)
    {
        if (x <= 0) return 0;
        if (x < a + 1)
        {
            double ap = a, sum = 1.0 / a, del = sum;
            for (int n = 1; n < 500; n++) { ap += 1; del *= x / ap; sum += del; if (Math.Abs(del) < Math.Abs(sum) * 1e-14) break; }
            return sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a));
        }
        double b = x + 1 - a, c = 1 / 1e-300, d = 1 / b, h = d;
        for (int i = 1; i < 500; i++)
        {
            double an = -i * (i - a);
            b += 2;
            d = an * d + b; if (Math.Abs(d) < 1e-300) d = 1e-300;
            c = b + an / c; if (Math.Abs(c) < 1e-300) c = 1e-300;
            d = 1 / d;
            double delta = d * c;
            h *= delta;
            if (Math.Abs(delta - 1) < 1e-14) break;
        }
        return 1 - Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * h;
    }

    public static double LogGamma(double x)
    {
        double[] c = { 76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5 };
        double y = x, tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        double ser = 1.000000000190015;
        for (int j = 0; j < 6; j++) ser += c[j] / ++y;
        return -tmp + Math.Log(2.5066282746310005 * ser / x);
    }

    /// <summary>Chi-square CDF: probability that a chi-square variable with df degrees of freedom is ≤ x.</summary>
    public static double ChiSquareCdf(double x, int df) => GammaP(df / 2.0, x / 2.0);
}
