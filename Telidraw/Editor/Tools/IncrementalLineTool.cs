// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor.Tools;

/// <summary>
/// Scribble tool — press, drag a continuous path, release. Captures pointer samples and
/// emits an IncrementalLine command. Uses a fixed small step (1/80 of unit screen width)
/// and motion code 01 (draw+step) for every sample, with meta opcodes inserted when the
/// sample direction flips quadrant (negate signDx / signDy).
/// </summary>
public class IncrementalLineTool : EditorToolBase
{
    public override string Name => "IncrementalLine";

    private readonly List<(float X, float Y)> _samples = [];

    public override void OnPointerPressed(float normX, float normY, bool isRightButton)
    {
        _samples.Clear();
        _samples.Add((normX, normY));
        StartX = normX;
        StartY = normY;
        CurrentX = normX;
        CurrentY = normY;
        IsDragging = true;
    }

    public override void OnPointerMoved(float normX, float normY)
    {
        if (!IsDragging) { return; }

        // Keep sample density reasonable — skip if we haven't moved at least ~1 pixel of
        // unit-screen. Overly dense samples explode the motion-code stream without adding
        // fidelity.
        var last = _samples[^1];
        float dx = normX - last.X;
        float dy = normY - last.Y;
        if (dx * dx + dy * dy < 0.0001f) { return; }

        _samples.Add((normX, normY));
        CurrentX = normX;
        CurrentY = normY;
    }

    public override List<(byte opcode, NaplpsOperands operands)> OnPointerReleased(float normX, float normY)
    {
        IsDragging = false;
        if (_samples.Count < 2) { return []; }

        var result = new List<(byte, NaplpsOperands)>
        {
            NaplpsCommandBuilder.BuildPointSetAbsolute(_samples[0].X, _samples[0].Y),
        };

        var codes = BuildMotionCodesFromSamples(_samples, out float stepDx, out float stepDy);
        result.Add(NaplpsCommandBuilder.BuildIncrementalLine(stepDx, stepDy, codes));

        return result;
    }

    public override string? ToolHint => IsDragging
        ? $"Scribble: drag a free-form path → committed as a uniform-step polyline. {_samples.Count} sample(s)."
        : "Scribble: press and drag to trace a polyline (IncrementalLine motion codes).";

    public override ToolPreview? GetPreview()
    {
        if (!IsDragging || _samples.Count < 2) { return null; }

        // Preview the QUANTIZED staircase the commit will actually emit — NOT the raw samples.
        // The mismatch between a smooth drag and the uniform-step result is exactly what made
        // the tool feel "ambiguous"; replaying the same motion codes makes preview == result.
        var codes = BuildMotionCodesFromSamples(_samples, out float stepDx, out float stepDy);
        var pts = IntegrateMotionCodes(_samples[0], stepDx, stepDy, codes);
        var preview = new ToolPreview { Shape = PreviewShape.Polygon };
        foreach (var p in pts) { preview.Points.Add(p); }
        return preview;
    }

    public override void Reset()
    {
        _samples.Clear();
        base.Reset();
    }

    /// <summary>
    /// Convert a sample-point polyline into the (stepDx, stepDy, codes[]) triple that
    /// BuildIncrementalLine expects. Simple strategy: pick the median per-sample delta as
    /// the step size, emit code 01 (step+draw) for each sample, insert meta 00 01 / 00 10
    /// when sign flips — produces a working but not optimal encoding.
    /// </summary>
    internal static byte[] BuildMotionCodesFromSamples(List<(float X, float Y)> samples, out float stepDx, out float stepDy)
    {
        // Step size: average sample-to-sample distance. Use absolute value since signs
        // are tracked by meta opcodes, not the step itself.
        float sumDx = 0, sumDy = 0;
        int n = samples.Count - 1;
        for (int i = 0; i < n; i++)
        {
            sumDx += System.MathF.Abs(samples[i + 1].X - samples[i].X);
            sumDy += System.MathF.Abs(samples[i + 1].Y - samples[i].Y);
        }
        stepDx = System.MathF.Max(0.001f, sumDx / System.Math.Max(1, n));
        stepDy = System.MathF.Max(0.001f, sumDy / System.Math.Max(1, n));

        var codes = new List<byte>();
        int signX = 1, signY = 1;

        for (int i = 0; i < n; i++)
        {
            float dx = samples[i + 1].X - samples[i].X;
            float dy = samples[i + 1].Y - samples[i].Y;
            int wantSx = System.MathF.Sign(dx) >= 0 ? 1 : -1;
            int wantSy = System.MathF.Sign(dy) >= 0 ? 1 : -1;

            if (wantSx != signX)
            {
                codes.Add(0x00); codes.Add(0x01);  // meta: negate signDx
                signX = wantSx;
            }
            if (wantSy != signY)
            {
                codes.Add(0x00); codes.Add(0x02);  // meta: negate signDy
                signY = wantSy;
            }

            codes.Add(0x01);  // step + draw
        }

        return codes.ToArray();
    }

    /// <summary>Replay the (step, codes) staircase produced by
    /// <see cref="BuildMotionCodesFromSamples"/> back into a polyline, so a preview can show the
    /// exact quantized path the command encodes. Mirrors the encoder's own conventions:
    /// 0x01 = step+draw with the current sign; meta 0x00,0x01 negates signX; 0x00,0x02 negates signY.</summary>
    internal static List<(float X, float Y)> IntegrateMotionCodes((float X, float Y) start, float stepDx, float stepDy, byte[] codes)
    {
        var pts = new List<(float X, float Y)> { start };
        float x = start.X, y = start.Y;
        int signX = 1, signY = 1;

        for (int i = 0; i < codes.Length; i++)
        {
            if (codes[i] == 0x00)
            {
                i++;
                if (i >= codes.Length) { break; }
                if (codes[i] == 0x01) { signX = -signX; }
                else if (codes[i] == 0x02) { signY = -signY; }
                continue;
            }

            x += signX * stepDx;
            y += signY * stepDy;
            pts.Add((x, y));
        }

        return pts;
    }
}
