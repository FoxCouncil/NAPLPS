// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using NAPLPS.Commands;
using NAPLPS.Telidraw;
using System.Numerics;

namespace NAPLPSTests.Telidraw;

/// <summary>
/// Issue #56: `line` and `line-rel` take a series of x/y pairs chained into ONE NAPLPS
/// LINE command (X3.110 5.3.3.2), not just a single end point. The bluestars files are
/// the reproduction from the issue report: bas (absolute star) and brs (relative star),
/// where the .nap files carry the reference geometry the .td files must compile to.
/// </summary>
[TestClass]
public class MultiPairLineTests
{
    private static string RrcookDir => Path.Combine(AppContext.BaseDirectory, "examples", "rrcook");

    private static NaplpsFormat Compile(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var parser = new Parser(tokens);
        var program = parser.Parse();
        Assert.AreEqual(0, parser.Diagnostics.Count, $"Parse errors: {string.Join("; ", parser.Diagnostics)}");

        var compiler = new Compiler(program);
        var format = compiler.Compile();
        Assert.AreEqual(0, compiler.Diagnostics.Count, $"Compile errors: {string.Join("; ", compiler.Diagnostics)}");

        return format;
    }

    [TestMethod]
    public void Compile_LineMultiPair_EqualsHandBuilt()
    {
        var compiled = Compile("""
            move 0.1 0.1
            line 0.5 0.4 0.2 0.8 0.9 0.9
            """);

        var reference = NaplpsFormat.New();
        var m = NaplpsCommandBuilder.BuildPointSetAbsolute(0.1f, 0.1f);
        reference.AddCommand(m.opcode, m.operands);
        var l = NaplpsCommandBuilder.BuildLineAbsolute([new Vector3(0.5f, 0.4f, 0), new Vector3(0.2f, 0.8f, 0), new Vector3(0.9f, 0.9f, 0)]);
        reference.AddCommand(l.opcode, l.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_LineRelMultiPair_EqualsHandBuilt()
    {
        var compiled = Compile("""
            move 0.5 0.5
            line-rel 0.1 0.2 -0.3 0.1 0.2 -0.2
            """);

        var reference = NaplpsFormat.New();
        var m = NaplpsCommandBuilder.BuildPointSetAbsolute(0.5f, 0.5f);
        reference.AddCommand(m.opcode, m.operands);
        var l = NaplpsCommandBuilder.BuildLineRelative([new Vector3(0.1f, 0.2f, 0), new Vector3(-0.3f, 0.1f, 0), new Vector3(0.2f, -0.2f, 0)]);
        reference.AddCommand(l.opcode, l.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_LineMultiPair_PenTracksLastVertex()
    {
        // The arc start point is computed from the compiler's pen tracker; if `line` left
        // the pen at the first pair instead of the last, the emitted arc deltas would shift.
        var compiled = Compile("""
            move 0.1 0.1
            line 0.5 0.4 0.2 0.8
            arc-outline 0.3 0.7 0.4 0.6
            """);

        var reference = NaplpsFormat.New();
        var m = NaplpsCommandBuilder.BuildPointSetAbsolute(0.1f, 0.1f);
        reference.AddCommand(m.opcode, m.operands);
        var l = NaplpsCommandBuilder.BuildLineAbsolute([new Vector3(0.5f, 0.4f, 0), new Vector3(0.2f, 0.8f, 0)]);
        reference.AddCommand(l.opcode, l.operands);
        var a = NaplpsCommandBuilder.BuildArcOutlined(0.3f - 0.2f, 0.7f - 0.8f, 0.4f - 0.3f, 0.6f - 0.7f);
        reference.AddCommand(a.opcode, a.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_LineOddArgCount_IsDiagnosticError()
    {
        var tokens = new Lexer("line 0.1 0.2 0.3").Tokenize();
        var program = new Parser(tokens).Parse();
        var compiler = new Compiler(program);
        compiler.Compile();

        Assert.AreEqual(1, compiler.Diagnostics.Count);
        Assert.IsTrue(compiler.Diagnostics[0].Message.Contains("pairs"));
    }

    [TestMethod]
    public void BlueStarAbsolute_TdMatchesReferenceNapGeometry()
    {
        AssertTdMatchesNapLineGeometry("bas", typeof(LineAbsoluteCommand));
    }

    [TestMethod]
    public void BlueStarRelative_TdMatchesReferenceNapGeometry()
    {
        AssertTdMatchesNapLineGeometry("brs", typeof(LineRelativeCommand));
    }

    /// <summary>
    /// Compiles the issue's .td source and parses its author-provided .nap translation,
    /// then requires the same single multi-block LINE command with the same rendered
    /// vertices. The byte streams legitimately differ (header/color/domain encoding
    /// choices), so the comparison is geometric, at the parsed-command level.
    /// </summary>
    private static void AssertTdMatchesNapLineGeometry(string name, Type lineType)
    {
        var tdSource = System.IO.File.ReadAllText(Path.Combine(RrcookDir, name + ".td"));
        var compiled = Compile(tdSource);
        var compiledBytes = compiled.ToBytes();

        var fromTd = NaplpsFormat.FromBytes(compiledBytes);
        var fromNap = NaplpsFormat.FromFile(Path.Combine(RrcookDir, name + ".nap"));

        var tdLines = fromTd.Commands.Select(s => s.Command).Where(lineType.IsInstanceOfType).Cast<LineCommand>().ToList();
        var napLines = fromNap.Commands.Select(s => s.Command).Where(lineType.IsInstanceOfType).Cast<LineCommand>().ToList();

        Assert.AreEqual(1, napLines.Count, $"{name}.nap reference should hold one LINE command");
        Assert.AreEqual(1, tdLines.Count, $"{name}.td should compile to one LINE command, not one per pair");

        // Points is the resolved absolute pen path (start + one point per coordinate
        // block), so the comparison is meaningful across absolute and relative encodings.
        var expected = napLines[0].Points;
        var actual = tdLines[0].Points;

        Assert.AreEqual(expected.Count, actual.Count, $"{name}: pen path point count");

        for (int i = 0; i < expected.Count; i++)
        {
            Assert.AreEqual(expected[i].X, actual[i].X, 1f / 256f, $"{name}: point {i} X");
            Assert.AreEqual(expected[i].Y, actual[i].Y, 1f / 256f, $"{name}: point {i} Y");
        }
    }
}
