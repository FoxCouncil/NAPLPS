// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSTests.File;

/// <summary>
/// X3.110 5.5 macro splice semantics: a macro call behaves as if the body bytes replaced
/// the invocation byte in the coded stream, so command operands flow across the splice
/// boundary in both directions. The Prodigy logon template TL80TB10.NAP leans on this:
/// bodies ending in a bare LINE opcode take vertices from the bytes after the invocation,
/// and bodies beginning with numeric data extend the command before the invocation.
/// </summary>
[TestClass]
public class MacroSpliceTests
{
    // Designate the macro set into G3 (ESC 2/15 7/10) and lock it into GL (LS3, ESC 6/15),
    // the same prologue TL80TB10 uses. GR stays on the PDI set for opcodes and numerics.
    private static readonly byte[] MacroPrologue = [0x1B, 0x2F, 0x7A, 0x1B, 0x6F];

    private static byte[] Stream(params byte[][] parts)
    {
        return [.. parts.SelectMany(p => p)];
    }

    [TestMethod]
    public void BodyEndingInBareOpcode_TakesOperandsAfterInvocation()
    {
        // DEF MACRO '!' with body = bare LINE SET (Relative) opcode, END, then the
        // invocation followed by six numeric bytes: the expanded LINE gets the operands.
        var stream = Stream(
            MacroPrologue,
            [0x80, 0x21, 0xAB, 0x85],
            [0x21, 0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5]);

        var format = NaplpsFormat.FromBytes(stream);

        var line = format.Commands.Single(s => s.Command.OpCode == 0xAB && s.IsSynthetic);
        CollectionAssert.AreEqual(new byte[] { 0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5 }, line.Command.Operands.ToArray());
        CollectionAssert.AreEqual(stream, format.ToBytes(), "operand continuation must stay byte-exact");
    }

    [TestMethod]
    public void BodyBeginningWithNumerics_ExtendsCommandBeforeInvocation()
    {
        // DEF MACRO '!' with body = three numeric bytes, END, then LINE (Relative) with
        // three inline operand bytes followed by the invocation: the spliced body numerics
        // continue the LINE's operand string to six bytes.
        var stream = Stream(
            MacroPrologue,
            [0x80, 0x21, 0xC3, 0xC4, 0xC5, 0x85],
            [0xA9, 0xC0, 0xC1, 0xC2, 0x21]);

        var format = NaplpsFormat.FromBytes(stream);

        var line = format.Commands.Single(s => s.Command.OpCode == 0xA9);
        CollectionAssert.AreEqual(new byte[] { 0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5 }, line.Command.Operands.ToArray());
        CollectionAssert.AreEqual(stream, format.ToBytes(), "mid-operand invocation must stay byte-exact");
    }

    [TestMethod]
    public void CancelInsideExpansion_DropsRemainingBodyBytes()
    {
        // Body = LINE + operands, CAN, then a second LINE + operands. CAN terminates the
        // executing macro immediately: only the first LINE materializes.
        var stream = Stream(
            MacroPrologue,
            [0x80, 0x21, 0xA9, 0xC0, 0xC1, 0xC2, 0x18, 0xA9, 0xC3, 0xC4, 0xC5, 0x85],
            [0x21]);

        var format = NaplpsFormat.FromBytes(stream);

        Assert.AreEqual(1, format.Commands.Count(s => s.Command.OpCode == 0xA9 && s.IsSynthetic));
        CollectionAssert.AreEqual(stream, format.ToBytes());
    }

    [TestMethod]
    public void DefinitionOpenAtEndOfStream_RoundTripsBufferedBytes()
    {
        // A stray DEF MACRO byte with no END (e.g. the 0x80 inside a UTF-8 em-dash in an
        // embedded comment, or an ad trailer) must not swallow the rest of the stream.
        var stream = new byte[] { 0x41, 0x42, 0x80, 0x94, 0x43, 0x44, 0x45 };

        var format = NaplpsFormat.FromBytes(stream);

        CollectionAssert.AreEqual(stream, format.ToBytes(), "open definition buffer must flush at EOF");
    }
}
