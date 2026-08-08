// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using NAPLPS.Telidraw;
using System.Numerics;

namespace NAPLPSTests.Telidraw;

/// <summary>
/// Golden tests for the Telidraw compiler: each test compiles a small source snippet and
/// verifies the resulting NAPLPS byte stream matches a hand-built equivalent produced by
/// NaplpsCommandBuilder directly. Byte equality is the contract the decompiler (Phase 8)
/// relies on for round-trips.
/// </summary>
[TestClass]
public class CompilerTests
{
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

    private static byte[] TailBytesAfterFormatHeader(NaplpsFormat format)
    {
        // NaplpsFormat.New() seeds the stream with Cancel + NonSelectiveReset sentinels
        // (3 bytes total). Compiler output appends after those. The hand-built reference
        // is built the same way, so both streams compare byte-for-byte including the header.
        return format.ToBytes();
    }

    /// <summary>Compiles bare with an explicit system type - the only path that reaches
    /// the Compiler's ApplySystemDefaults branch and the forced re-parse.</summary>
    private static byte[] CompileBare(string src, NaplpsSystemType? systemType)
    {
        var program = new Parser(new Lexer(src).Tokenize()).Parse();
        var compiler = new Compiler(program, systemType) { BareFormat = true };
        var format = compiler.Compile();
        Assert.AreEqual(0, compiler.Diagnostics.Count, string.Join("; ", compiler.Diagnostics));
        return format.ToBytes();
    }

    /// <summary>
    /// An explicit system type must shape the bare encoding state (Compiler.cs ApplySystemDefaults
    /// branch): Telidon's coordinate precision (multi-byte value 4) differs from NAPLPS's (3), so
    /// the same coordinate-bearing source encodes to DIFFERENT bytes. The raw statement sets
    /// _emittedRaw, so Compile() also takes the forced FromBytes re-parse with the explicit
    /// system type. This is the CLI's `compile --system-type=` path, previously untested.
    /// </summary>
    [TestMethod]
    public void CompileBare_ExplicitSystemType_ShapesEncoding()
    {
        const string src = "move 0.3 0.4\nline 0.7 0.9\nraw 14\n";

        var naplps = CompileBare(src, NaplpsSystemType.NAPLPS);
        var telidon = CompileBare(src, NaplpsSystemType.Telidon);
        var defaulted = CompileBare(src, null);

        CollectionAssert.AreEqual(naplps, defaulted, "null system type must behave as NAPLPS");
        CollectionAssert.AreNotEqual(naplps, telidon,
            "Telidon's higher coordinate precision must change the bare encoding");
    }

    [TestMethod]
    public void Compile_MoveAbsolute_EqualsHandBuilt()
    {
        var compiled = Compile("move 0.5 0.5");

        var reference = NaplpsFormat.New();
        var (op, ops) = NaplpsCommandBuilder.BuildPointSetAbsolute(0.5f, 0.5f);
        reference.AddCommand(op, ops);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_LineAbsolute_EqualsHandBuilt()
    {
        var compiled = Compile("""
            move 0.1 0.1
            line 0.5 0.4
            """);

        var reference = NaplpsFormat.New();
        var m = NaplpsCommandBuilder.BuildPointSetAbsolute(0.1f, 0.1f);
        reference.AddCommand(m.opcode, m.operands);
        var l = NaplpsCommandBuilder.BuildLineAbsolute(0.5f, 0.4f);
        reference.AddCommand(l.opcode, l.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_RectFilled_EqualsHandBuilt()
    {
        var compiled = Compile("""
            move 0.1 0.1
            rect 0.3 0.2
            """);

        var reference = NaplpsFormat.New();
        var m = NaplpsCommandBuilder.BuildPointSetAbsolute(0.1f, 0.1f);
        reference.AddCommand(m.opcode, m.operands);
        var r = NaplpsCommandBuilder.BuildRectangleFilled(0.3f, 0.2f);
        reference.AddCommand(r.opcode, r.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_Color_EqualsHandBuilt()
    {
        var compiled = Compile("color 5");

        var reference = NaplpsFormat.New();
        var c = NaplpsCommandBuilder.BuildSelectColor(5);
        reference.AddCommand(c.opcode, c.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_WithColor_EmitsExplicitRestore()
    {
        // with color 3 { rect 0.2 0.2 }  →  SelectColor(3); Rect; SelectColor(7=prevDefault)
        var compiled = Compile("with color 3 { move 0.1 0.1  rect 0.2 0.2 }");

        var reference = NaplpsFormat.New();
        var c3 = NaplpsCommandBuilder.BuildSelectColor(3);
        reference.AddCommand(c3.opcode, c3.operands);
        var m = NaplpsCommandBuilder.BuildPointSetAbsolute(0.1f, 0.1f);
        reference.AddCommand(m.opcode, m.operands);
        var r = NaplpsCommandBuilder.BuildRectangleFilled(0.2f, 0.2f);
        reference.AddCommand(r.opcode, r.operands);
        var c7 = NaplpsCommandBuilder.BuildSelectColor(7);
        reference.AddCommand(c7.opcode, c7.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_RepeatUnrolls()
    {
        var compiled = Compile("""
            move 0.1 0.1
            repeat 3 {
              line 0.2 0.2
            }
            """);

        var reference = NaplpsFormat.New();
        var m = NaplpsCommandBuilder.BuildPointSetAbsolute(0.1f, 0.1f);
        reference.AddCommand(m.opcode, m.operands);

        for (int i = 0; i < 3; i++)
        {
            var l = NaplpsCommandBuilder.BuildLineAbsolute(0.2f, 0.2f);
            reference.AddCommand(l.opcode, l.operands);
        }

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_ForLoop_UnrollsAndBindsVariable()
    {
        var compiled = Compile("for i in 1..3 { color i }");

        var reference = NaplpsFormat.New();
        for (byte i = 1; i <= 3; i++)
        {
            var c = NaplpsCommandBuilder.BuildSelectColor(i);
            reference.AddCommand(c.opcode, c.operands);
        }

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_PaletteAlias_ResolvesInColor()
    {
        var compiled = Compile("""
            palette cyan = 11
            color cyan
            """);

        var reference = NaplpsFormat.New();
        var c = NaplpsCommandBuilder.BuildSelectColor(11);
        reference.AddCommand(c.opcode, c.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_LetBinding_UsedInCommand()
    {
        var compiled = Compile("""
            let w = 0.4
            let h = 0.3
            move 0.1 0.1
            rect w h
            """);

        var reference = NaplpsFormat.New();
        var m = NaplpsCommandBuilder.BuildPointSetAbsolute(0.1f, 0.1f);
        reference.AddCommand(m.opcode, m.operands);
        var r = NaplpsCommandBuilder.BuildRectangleFilled(0.4f, 0.3f);
        reference.AddCommand(r.opcode, r.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_ExpressionArithmetic_InArgs()
    {
        var compiled = Compile("move (0.1 + 0.2) (0.3 * 2)");

        var reference = NaplpsFormat.New();
        var m = NaplpsCommandBuilder.BuildPointSetAbsolute(0.3f, 0.6f);
        reference.AddCommand(m.opcode, m.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_ProcInline_SubstitutesArgs()
    {
        var compiled = Compile("""
            proc box(x, y, size) {
              move x y
              rect size size
            }

            box 0.2 0.2 0.3
            """);

        var reference = NaplpsFormat.New();
        var m = NaplpsCommandBuilder.BuildPointSetAbsolute(0.2f, 0.2f);
        reference.AddCommand(m.opcode, m.operands);
        var r = NaplpsCommandBuilder.BuildRectangleFilled(0.3f, 0.3f);
        reference.AddCommand(r.opcode, r.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_IfTrueBranch_Emits()
    {
        var compiled = Compile("if 1 { color 5 } else { color 3 }");

        var reference = NaplpsFormat.New();
        var c = NaplpsCommandBuilder.BuildSelectColor(5);
        reference.AddCommand(c.opcode, c.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_IfFalseBranch_Emits()
    {
        var compiled = Compile("if 0 { color 5 } else { color 3 }");

        var reference = NaplpsFormat.New();
        var c = NaplpsCommandBuilder.BuildSelectColor(3);
        reference.AddCommand(c.opcode, c.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_Reset_EmitsResetCommand()
    {
        var compiled = Compile("reset");

        var reference = NaplpsFormat.New();
        var r = NaplpsCommandBuilder.BuildReset();
        reference.AddCommand(r.opcode, r.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_Wait_EmitsWaitInterval()
    {
        var compiled = Compile("wait 20");

        var reference = NaplpsFormat.New();
        var w = NaplpsCommandBuilder.BuildWait(20);
        reference.AddCommand(w.opcode, w.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_Field_FullScreenFromZeroArgs()
    {
        var compiled = Compile("field");

        var reference = NaplpsFormat.New();
        var f = NaplpsCommandBuilder.BuildField();
        reference.AddCommand(f.opcode, f.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_Directive_CoordPixels_ScalesCoordinates()
    {
        // With #coord pixels and the default 256x192 resolution, "move 128 96" should
        // match the fractions version "move 0.5 0.5".
        var compiled = Compile("""
            #coord pixels
            move 128 96
            """);

        var reference = NaplpsFormat.New();
        var m = NaplpsCommandBuilder.BuildPointSetAbsolute(0.5f, 0.5f);
        reference.AddCommand(m.opcode, m.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_NestedRepeatWithWith_RestoresColor()
    {
        var compiled = Compile("""
            repeat 2 {
              with color 3 {
                move 0.1 0.1
              }
            }
            """);

        var reference = NaplpsFormat.New();
        for (int i = 0; i < 2; i++)
        {
            var c3 = NaplpsCommandBuilder.BuildSelectColor(3);
            reference.AddCommand(c3.opcode, c3.operands);
            var m = NaplpsCommandBuilder.BuildPointSetAbsolute(0.1f, 0.1f);
            reference.AddCommand(m.opcode, m.operands);
            var c7 = NaplpsCommandBuilder.BuildSelectColor(7);
            reference.AddCommand(c7.opcode, c7.operands);
        }

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }

    [TestMethod]
    public void Compile_PolygonFilled_UsesRelativeVertices()
    {
        var compiled = Compile("""
            move 0.2 0.2
            polygon 0.3 0.2  0.3 0.3  0.2 0.3
            """);

        var reference = NaplpsFormat.New();
        var m = NaplpsCommandBuilder.BuildPointSetAbsolute(0.2f, 0.2f);
        reference.AddCommand(m.opcode, m.operands);
        var verts = new[]
        {
            new Vector3(0.3f - 0.2f, 0.2f - 0.2f, 0),   // rel from pen
            new Vector3(0.3f - 0.3f, 0.3f - 0.2f, 0),   // rel from prev
            new Vector3(0.2f - 0.3f, 0.3f - 0.3f, 0),
        };
        var p = NaplpsCommandBuilder.BuildPolygonFilled(verts);
        reference.AddCommand(p.opcode, p.operands);

        CollectionAssert.AreEqual(reference.ToBytes(), compiled.ToBytes());
    }
}
