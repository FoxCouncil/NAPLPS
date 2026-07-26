// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSTests.Base;

/// <summary>
/// DetMath exists to be identical everywhere, which no single-platform test can prove. What these
/// tests CAN establish is the other half of the contract: that it agrees with the platform libm
/// closely enough to be a drop-in, so swapping it into the renderer is a determinism change and
/// not a geometry change. The cross-platform half is enforced by the CI matrix.
/// </summary>
[TestClass]
public class DetMathTests
{
    /// <summary>Tolerance in units in the last place. 1 ULP is the best any libm promises.</summary>
    private const double MaxUlps = 2.0;

    /// <summary>Angles the arc renderer actually produces: Atan2 output, sweeps, and full circles.</summary>
    private static IEnumerable<double> RendererAngles()
    {
        for (int i = -2000; i <= 2000; i++)
        {
            yield return i * (Math.PI / 500.0);
        }
    }

    /// <summary>Distance between two doubles expressed in ULPs of the larger one.</summary>
    private static double UlpsApart(double a, double b)
    {
        if (a == b)
        {
            return 0.0;
        }

        double magnitude = Math.Max(Math.Abs(a), Math.Abs(b));
        double ulp = Math.BitIncrement(magnitude) - magnitude;

        return Math.Abs(a - b) / ulp;
    }

    [TestMethod]
    public void SinMatchesPlatformLibm()
    {
        double worst = 0;
        double worstAt = 0;

        foreach (var a in RendererAngles())
        {
            // Near a zero of sine the result is denormal-small relative to the input, so an
            // ULP measure is meaningless there; compare absolutely instead.
            double mine = DetMath.Sin(a);
            double theirs = Math.Sin(a);
            double err = Math.Abs(mine) < 1e-8 ? Math.Abs(mine - theirs) / 1e-16 : UlpsApart(mine, theirs);

            if (err > worst)
            {
                worst = err;
                worstAt = a;
            }
        }

        Assert.IsLessThanOrEqualTo(MaxUlps, worst, $"Sin deviates from libm by {worst:F2} ULP at {worstAt}");
    }

    [TestMethod]
    public void CosMatchesPlatformLibm()
    {
        double worst = 0;
        double worstAt = 0;

        foreach (var a in RendererAngles())
        {
            double mine = DetMath.Cos(a);
            double theirs = Math.Cos(a);
            double err = Math.Abs(mine) < 1e-8 ? Math.Abs(mine - theirs) / 1e-16 : UlpsApart(mine, theirs);

            if (err > worst)
            {
                worst = err;
                worstAt = a;
            }
        }

        Assert.IsLessThanOrEqualTo(MaxUlps, worst, $"Cos deviates from libm by {worst:F2} ULP at {worstAt}");
    }

    [TestMethod]
    public void Atan2MatchesPlatformLibm()
    {
        double worst = 0;
        string worstAt = "";

        for (int iy = -60; iy <= 60; iy++)
        {
            for (int ix = -60; ix <= 60; ix++)
            {
                double y = iy * 0.37;
                double x = ix * 0.41;

                double err = UlpsApart(DetMath.Atan2(y, x), Math.Atan2(y, x));

                if (err > worst)
                {
                    worst = err;
                    worstAt = $"({y}, {x})";
                }
            }
        }

        Assert.IsLessThanOrEqualTo(MaxUlps, worst, $"Atan2 deviates from libm by {worst:F2} ULP at {worstAt}");
    }

    [TestMethod]
    public void PythagoreanIdentityHolds()
    {
        foreach (var a in RendererAngles())
        {
            double s = DetMath.Sin(a);
            double c = DetMath.Cos(a);

            Assert.IsLessThan(1e-15, Math.Abs(s * s + c * c - 1.0), $"sin^2+cos^2 off at {a}");
        }
    }

    [TestMethod]
    public void CardinalAnglesAreExact()
    {
        // The renderer folds circles onto these; they must not drift, or a full circle
        // fails to close on itself.
        Assert.AreEqual(0.0, DetMath.Sin(0.0), 0.0);
        Assert.AreEqual(1.0, DetMath.Cos(0.0), 0.0);
        Assert.IsLessThan(1e-15, Math.Abs(DetMath.Sin(Math.PI)));
        Assert.IsLessThan(1e-15, Math.Abs(DetMath.Cos(Math.PI) + 1.0));
        Assert.IsLessThan(1e-15, Math.Abs(DetMath.Sin(Math.PI / 2) - 1.0));
        Assert.IsLessThan(1e-15, Math.Abs(DetMath.Cos(Math.PI / 2)));
    }

    [TestMethod]
    public void Atan2AxesMatchLibm()
    {
        Assert.AreEqual(Math.Atan2(0.0, 1.0), DetMath.Atan2(0.0, 1.0), 0.0);
        Assert.AreEqual(Math.Atan2(0.0, -1.0), DetMath.Atan2(0.0, -1.0), 0.0);
        Assert.AreEqual(Math.Atan2(1.0, 0.0), DetMath.Atan2(1.0, 0.0), 1e-16);
        Assert.AreEqual(Math.Atan2(-1.0, 0.0), DetMath.Atan2(-1.0, 0.0), 1e-16);
        Assert.AreEqual(Math.Atan2(-0.0, -1.0), DetMath.Atan2(-0.0, -1.0), 0.0);
    }

    [TestMethod]
    public void RepeatedCallsAreStable()
    {
        // Guards against anything stateful or order-dependent creeping into the kernels.
        foreach (var a in RendererAngles())
        {
            Assert.AreEqual(DetMath.Sin(a), DetMath.Sin(a), 0.0);
            Assert.AreEqual(DetMath.Cos(a), DetMath.Cos(a), 0.0);
            Assert.AreEqual(DetMath.Atan2(a, 1.5), DetMath.Atan2(a, 1.5), 0.0);
        }
    }

    /// <summary>
    /// The failure mode issue #45 cares about most: the arc tessellator turns an angle into an
    /// integer step count, so a sub-ULP wobble that straddles an integer boundary changes the
    /// whole point set rather than one pixel. Pin that the two agree on the integer.
    /// </summary>
    [TestMethod]
    public void ArcStepCountsAgreeWithLibm()
    {
        int checkedCases = 0;

        // Walk real arc geometry: a centre, a radius, and three points on the circle, exactly the
        // shape DrawableArc reduces to angles before deciding how many segments to tessellate into.
        for (int r = 8; r <= 400; r += 7)
        {
            for (int startDeg = 0; startDeg < 360; startDeg += 13)
            {
                for (int sweepDeg = 5; sweepDeg < 360; sweepDeg += 17)
                {
                    double start = startDeg * Math.PI / 180.0;
                    double end = (startDeg + sweepDeg) * Math.PI / 180.0;

                    float sy = (float)(r * Math.Sin(start));
                    float sx = (float)(r * Math.Cos(start));
                    float ey = (float)(r * Math.Sin(end));
                    float ex = (float)(r * Math.Cos(end));

                    float mineSweep = DetMath.Atan2(ey, ex) - DetMath.Atan2(sy, sx);
                    float theirsSweep = MathF.Atan2(ey, ex) - MathF.Atan2(sy, sx);

                    int mine = Math.Max(32, (int)(MathF.Abs(mineSweep) * r));
                    int theirs = Math.Max(32, (int)(MathF.Abs(theirsSweep) * r));

                    Assert.AreEqual(theirs, mine, $"step count diverges at radius {r}, start {startDeg}, sweep {sweepDeg}");
                    checkedCases++;
                }
            }
        }

        Assert.IsGreaterThan(10000, checkedCases);
    }
}
