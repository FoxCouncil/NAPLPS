// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSTests.File;

/// <summary>
/// Bounds on recursive macro expansion. X3.110 is silent on a limit, so nothing stops a
/// hostile (or corrupt) stream from defining a macro that invokes itself: on the splice
/// path the injection queue refills forever and the parse never returns; on the recursive
/// descent path (DEFP define-and-display replay) the sub-parse recurses until the process
/// dies by uncatchable StackOverflowException. Both must terminate with a recorded stream
/// error instead.
/// </summary>
[TestClass]
public class MacroRecursionTests
{
    // Designate the macro set into G3 (ESC 2/15 7/10) and lock it into GL (LS3, ESC 6/15),
    // the same prologue TL80TB10 uses. GR stays on the PDI set for opcodes and numerics.
    private static readonly byte[] MacroPrologue = [0x1B, 0x2F, 0x7A, 0x1B, 0x6F];

    private static byte[] Stream(params byte[][] parts)
    {
        return [.. parts.SelectMany(p => p)];
    }

    [TestMethod]
    [Timeout(30000)]
    public void SelfInvokingSpliceMacro_TerminatesAndRoundTrips()
    {
        // DEF MACRO '!' whose body is its own invocation byte, then the invocation: the
        // splice would refill the injection queue on every expansion, forever.
        var stream = Stream(
            MacroPrologue,
            [0x80, 0x21, 0x21, 0x85],
            [0x21]);

        var format = NaplpsFormat.FromBytes(stream);

        Assert.IsTrue(format.State.Errors.Any(e => e.Type == NaplpsErrorType.InvalidCommand), "suppressed expansion must record a stream error");
        CollectionAssert.AreEqual(stream, format.ToBytes(), "suppression must not break byte round-trip");
    }

    [TestMethod]
    [Timeout(30000)]
    public void MutuallyRecursiveSpliceMacros_Terminate()
    {
        // '!' invokes '"', '"' invokes '!': neither expansion ever returns to the real stream.
        var stream = Stream(
            MacroPrologue,
            [0x80, 0x21, 0x22, 0x85],
            [0x80, 0x22, 0x21, 0x85],
            [0x21]);

        var format = NaplpsFormat.FromBytes(stream);

        Assert.IsTrue(format.State.Errors.Any(e => e.Type == NaplpsErrorType.InvalidCommand), "suppressed expansion must record a stream error");
        CollectionAssert.AreEqual(stream, format.ToBytes(), "suppression must not break byte round-trip");
    }

    [TestMethod]
    [Timeout(30000)]
    public void SelfInvokingMacroInOperandScan_Terminates()
    {
        // Body = numeric byte + self-invocation, invoked mid-operand: the operand scan in
        // ReadOperandsAsync keeps consuming the invocation byte and re-injecting the body.
        var stream = Stream(
            MacroPrologue,
            [0x80, 0x21, 0xC0, 0x21, 0x85],
            [0xA9, 0x21]);

        var format = NaplpsFormat.FromBytes(stream);

        Assert.IsTrue(format.State.Errors.Any(e => e.Type == NaplpsErrorType.InvalidCommand), "suppressed expansion must record a stream error");
    }

    [TestMethod]
    [Timeout(30000)]
    public void SelfInvokingSpliceMacro_StreamedFeed_Terminates()
    {
        // The same self-invoking stream over the wire path, one byte at a time.
        var stream = Stream(
            MacroPrologue,
            [0x80, 0x21, 0x21, 0x85],
            [0x21]);

        var state = new NaplpsState();
        NaplpsDecoder.ApplySystemDefaults(state, NaplpsSystemType.NAPLPS);
        var decoder = new NaplpsDecoder(state);

        foreach (var b in stream)
        {
            decoder.Feed([b]);
        }

        decoder.Flush();

        Assert.IsFalse(decoder.IsFaulted, "the wire path must survive a recursive macro");
        Assert.IsTrue(state.Errors.Any(e => e.Type == NaplpsErrorType.InvalidCommand), "suppressed expansion must record a stream error");
    }

    [TestMethod]
    [Timeout(30000)]
    public void SelfInvokingDefpMacro_DoesNotOverflowStack()
    {
        // DEFP MACRO '!' whose body invokes itself: the define-and-display replay would
        // recurse through the sub-parse with no depth guard and kill the process.
        var stream = Stream(
            MacroPrologue,
            [0x81, 0x21, 0x21, 0x85]);

        var format = NaplpsFormat.FromBytes(stream);

        Assert.IsTrue(format.State.Errors.Any(e => e.Type == NaplpsErrorType.InvalidCommand), "suppressed expansion must record a stream error");
        CollectionAssert.AreEqual(stream, format.ToBytes(), "suppression must not break byte round-trip");
    }
}
