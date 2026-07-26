// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS;

/// <summary>
/// Bit-reproducible replacements for the transcendental functions the renderer depends on.
///
/// IEEE 754 only requires +, -, *, / and sqrt to be correctly rounded, and .NET only guarantees
/// those. Sin/Cos/Atan2 are delegated to the platform C runtime — the Windows UCRT, Apple's libm
/// and glibc each carry their own polynomial kernels, so they disagree in the last unit in the
/// last place. That is enough to move a rendered pixel, and in one place it is enough to move
/// thousands: DrawableArc derives its tessellation step COUNT from an Atan2 result, so a 1 ULP
/// difference can change the number of arc points and retessellate the whole curve.
///
/// Everything below is built exclusively from the correctly-rounded operations, so it produces
/// identical bits on every platform and architecture .NET targets. The kernels are the classic
/// fdlibm ones (Sun Microsystems, 1993, freely redistributable): argument reduction against a
/// multi-word split of pi/2 whose leading terms have zeroed low mantissa bits — making the
/// products exact — followed by a fixed-order minimax polynomial evaluated in a fixed order.
///
/// Accuracy is under 1 ULP of <see cref="double"/>. The renderer consumes the results at
/// <see cref="float"/> precision and pixel scale, so the residual error is many orders of
/// magnitude below anything observable; the point of this class is agreement, not accuracy.
/// </summary>
public static class DetMath
{
    /// <summary>pi as a double, and the remainder that the double cannot hold.</summary>
    private const double Pi = 3.14159265358979311600e+00;
    private const double PiLo = 1.22464679914735317720e-16;

    private const double PiOver2 = 1.57079632679489655800e+00;

    /// <summary>2/pi, for computing how many quadrants to fold away.</summary>
    private const double TwoOverPi = 6.36619772367581382433e-01;

    // pi/2 split into three doubles. The first two have their low mantissa bits zeroed so that
    // n * term is EXACT for every quadrant count n the renderer can produce, which is what keeps
    // the subtraction from cancelling away the information we need. Together they carry ~166 bits
    // of pi/2 — far more than the |n| <= 8 that arc angles ever reach requires.
    private const double PiOver2_1 = 1.57079632673412561417e+00;
    private const double PiOver2_2 = 6.07710050630396597660e-11;
    private const double PiOver2_3 = 2.02226624879595063154e-21;

    // __kernel_sin minimax coefficients, |x| <= pi/4.
    private const double S1 = -1.66666666666666324348e-01;
    private const double S2 = 8.33333333332248946124e-03;
    private const double S3 = -1.98412698298579493134e-04;
    private const double S4 = 2.75573137070700676789e-06;
    private const double S5 = -2.50507602534068634195e-08;
    private const double S6 = 1.58969099521155010221e-10;

    // __kernel_cos minimax coefficients, |x| <= pi/4.
    private const double C1 = 4.16666666666666019037e-02;
    private const double C2 = -1.38888888888741095749e-03;
    private const double C3 = 2.48015872894767294178e-05;
    private const double C4 = -2.75573143513906633035e-07;
    private const double C5 = 2.08757232129817482790e-09;
    private const double C6 = -1.13596475577881948265e-11;

    // __kernel_atan minimax coefficients, |x| <= 0.4375.
    private const double T0 = 3.33333333333329318027e-01;
    private const double T1 = -1.99999999998764832476e-01;
    private const double T2 = 1.42857142725034663711e-01;
    private const double T3 = -1.11111104054623557880e-01;
    private const double T4 = 9.09088713343650656196e-02;
    private const double T5 = -7.69187620504482999495e-02;
    private const double T6 = 6.66107313738753120669e-02;
    private const double T7 = -5.83357013379057348645e-02;
    private const double T8 = 4.97687799461593236017e-02;
    private const double T9 = -3.65315727442169155270e-02;
    private const double T10 = 1.62858201153657823623e-02;

    // atan() at the four reduction anchors, each split high/low so the reconstruction keeps
    // the bits the high word alone would drop.
    private static readonly double[] AtanHi =
    [
        4.63647609000806093515e-01, // atan(0.5)
        7.85398163397448278999e-01, // atan(1.0)
        9.82793723247329054082e-01, // atan(1.5)
        1.57079632679489655800e+00, // atan(inf)
    ];

    private static readonly double[] AtanLo =
    [
        2.26987774529616870924e-17,
        3.06161699786838301793e-17,
        1.39033110312309984516e-17,
        6.12323399573676603587e-17,
    ];

    /// <summary>Sine of <paramref name="x"/> radians, identical on every platform.</summary>
    public static double Sin(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x))
        {
            return double.NaN;
        }

        var (r, quadrant) = ReduceToQuadrant(x);

        return quadrant switch
        {
            0 => KernelSin(r),
            1 => KernelCos(r),
            2 => -KernelSin(r),
            _ => -KernelCos(r),
        };
    }

    /// <summary>Cosine of <paramref name="x"/> radians, identical on every platform.</summary>
    public static double Cos(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x))
        {
            return double.NaN;
        }

        var (r, quadrant) = ReduceToQuadrant(x);

        return quadrant switch
        {
            0 => KernelCos(r),
            1 => -KernelSin(r),
            2 => -KernelCos(r),
            _ => KernelSin(r),
        };
    }

    /// <summary>Arc tangent of <paramref name="x"/>, identical on every platform.</summary>
    public static double Atan(double x)
    {
        if (double.IsNaN(x))
        {
            return double.NaN;
        }

        if (double.IsInfinity(x))
        {
            return x > 0 ? PiOver2 : -PiOver2;
        }

        bool negative = x < 0;
        double ax = negative ? -x : x;
        int id;

        // Fold the argument down to |x| <= 0.4375 around whichever anchor is closest, using the
        // tangent addition identity. Four intervals keeps the polynomial short without losing bits.
        if (ax < 0.4375)
        {
            id = -1;
        }
        else if (ax < 1.1875)
        {
            if (ax < 0.6875)
            {
                id = 0;
                ax = (2.0 * ax - 1.0) / (2.0 + ax);
            }
            else
            {
                id = 1;
                ax = (ax - 1.0) / (ax + 1.0);
            }
        }
        else if (ax < 2.4375)
        {
            id = 2;
            ax = (ax - 1.5) / (1.0 + 1.5 * ax);
        }
        else
        {
            id = 3;
            ax = -1.0 / ax;
        }

        double poly = KernelAtanPoly(ax);

        if (id < 0)
        {
            return negative ? -(ax - poly) : ax - poly;
        }

        double result = AtanHi[id] - ((poly - AtanLo[id]) - ax);

        return negative ? -result : result;
    }

    /// <summary>
    /// Two-argument arc tangent, identical on every platform. Follows the same special-case
    /// table as the C library so callers see no behavioural change at the axes.
    /// </summary>
    public static double Atan2(double y, double x)
    {
        if (double.IsNaN(y) || double.IsNaN(x))
        {
            return double.NaN;
        }

        if (y == 0.0)
        {
            // Sign of the zero decides which side of the branch cut we are on.
            bool xNegative = x < 0.0 || (x == 0.0 && double.IsNegative(x));

            if (!xNegative)
            {
                return y;
            }

            return double.IsNegative(y) ? -Pi : Pi;
        }

        if (x == 0.0)
        {
            return y > 0.0 ? PiOver2 : -PiOver2;
        }

        if (double.IsInfinity(x))
        {
            if (double.IsInfinity(y))
            {
                double diagonal = x > 0 ? 0.78539816339744827900e+00 : 2.35619449019234483700e+00;

                return y > 0 ? diagonal : -diagonal;
            }

            if (x > 0)
            {
                return y > 0 ? 0.0 : -0.0;
            }

            return y > 0 ? Pi : -Pi;
        }

        if (double.IsInfinity(y))
        {
            return y > 0 ? PiOver2 : -PiOver2;
        }

        double ratio = y / x;
        double z = Atan(ratio < 0 ? -ratio : ratio);

        if (x > 0)
        {
            return y > 0 ? z : -z;
        }

        // Reconstruct through the low word of pi so the far quadrants keep full precision.
        return y > 0 ? Pi - (z - PiLo) : (z - PiLo) - Pi;
    }

    /// <summary>Single-precision <see cref="Sin(double)"/>; computed in double, then rounded once.</summary>
    public static float Sin(float x) => (float)Sin((double)x);

    /// <summary>Single-precision <see cref="Cos(double)"/>; computed in double, then rounded once.</summary>
    public static float Cos(float x) => (float)Cos((double)x);

    /// <summary>Single-precision <see cref="Atan2(double, double)"/>; computed in double, then rounded once.</summary>
    public static float Atan2(float y, float x) => (float)Atan2((double)y, (double)x);

    /// <summary>
    /// Folds x into r + quadrant * pi/2 with |r| &lt;= pi/4, subtracting the three-word pi/2 in
    /// descending order so each product is exact and no significant bits are lost to cancellation.
    /// </summary>
    private static (double Remainder, int Quadrant) ReduceToQuadrant(double x)
    {
        double n = Math.Round(x * TwoOverPi);

        double r = x - n * PiOver2_1;
        r -= n * PiOver2_2;
        r -= n * PiOver2_3;

        // Math.Round returns a whole double; the cast is exact for every angle the renderer builds.
        int quadrant = (int)((long)n & 3);

        return (r, quadrant);
    }

    /// <summary>Sine on the reduced interval |x| &lt;= pi/4.</summary>
    private static double KernelSin(double x)
    {
        double z = x * x;
        double v = z * x;
        double r = S2 + z * (S3 + z * (S4 + z * (S5 + z * S6)));

        return x + v * (S1 + z * r);
    }

    /// <summary>Cosine on the reduced interval |x| &lt;= pi/4.</summary>
    private static double KernelCos(double x)
    {
        double z = x * x;
        double r = z * (C1 + z * (C2 + z * (C3 + z * (C4 + z * (C5 + z * C6)))));

        return 1.0 - (0.5 * z - z * r);
    }

    /// <summary>
    /// Returns x - atan(x) on |x| &lt;= 0.4375, the form the reconstruction above expects.
    /// Split into even and odd halves so the two chains evaluate independently.
    /// </summary>
    private static double KernelAtanPoly(double x)
    {
        double z = x * x;
        double w = z * z;
        double s1 = z * (T0 + w * (T2 + w * (T4 + w * (T6 + w * (T8 + w * T10)))));
        double s2 = w * (T1 + w * (T3 + w * (T5 + w * (T7 + w * T9))));

        return x * (s1 + s2);
    }
}
