// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using NAPLPS.Telidraw;

namespace NAPLPSTests.Telidraw;

/// <summary>
/// Issue #41 regressions: a .nap whose decompile falls back to raw command forms must
/// reload as a fully TYPED command list (raw placeholders serialize correctly but
/// neither apply state nor draw), and Prodigy system-type detection must see through
/// the CAN+NSR sentinels the non-bare compiler prepends.
/// </summary>
[TestClass]
public class RawReloadTests
{
    // The reporter's BLUELINE.nap: DOMAIN, TEXTURE, SELECT COLOR (blue), POINT SET
    // ABSOLUTE, LINE RELATIVE (two chained segments), trailing operand-less POINT SET
    // RELATIVE. Decompiles through raw fallback forms.
    private static readonly byte[] Blueline = Convert.FromHexString(
        "A1C8C0C0C9A3C0C0D2C0BECCA4C9D3E4A9C1D9C4C6D6E3A5");

    private static NaplpsFormat Reload(byte[] nap, bool bareFormat)
    {
        var td = Decompiler.Decompile(NaplpsFormat.FromBytes(nap));
        var tokens = new Lexer(td).Tokenize();
        var parser = new Parser(tokens);
        var ast = parser.Parse();
        Assert.AreEqual(0, parser.Diagnostics.Count,
            $"Parse errors: {string.Join("; ", parser.Diagnostics)}");
        var compiler = new Compiler(ast) { BareFormat = bareFormat };
        var format = compiler.Compile();
        Assert.AreEqual(0, compiler.Diagnostics.Count,
            $"Compile errors: {string.Join("; ", compiler.Diagnostics)}");
        return format;
    }

    /// <summary>
    /// Raw-form commands must come back as their typed classes after reload, so that
    /// state (DOMAIN, TEXTURE) applies and geometry (LINE RELATIVE) draws. The typed
    /// sequence must match a direct parse of the original bytes.
    /// </summary>
    [TestMethod]
    public void RawCommands_ReloadTyped_BareFormat()
    {
        var direct = NaplpsFormat.FromBytes(Blueline);
        var reloaded = Reload(Blueline, bareFormat: true);

        CollectionAssert.AreEqual(
            direct.Commands.Select(c => c.Command.GetType()).ToList(),
            reloaded.Commands.Select(c => c.Command.GetType()).ToList());

        CollectionAssert.AreEqual(Blueline, reloaded.ToBytes(),
            "byte round-trip must stay exact");
    }

    /// <summary>
    /// The app's .td open path compiles without BareFormat, which prepends the CAN+NSR
    /// sentinels. The drawing commands after the sentinels must still be typed and the
    /// byte tail must round-trip exactly.
    /// </summary>
    [TestMethod]
    public void RawCommands_ReloadTyped_WithSentinels()
    {
        var direct = NaplpsFormat.FromBytes(Blueline);
        var reloaded = Reload(Blueline, bareFormat: false);

        // Skip the two leading control sentinels; the rest must be the typed sequence.
        var tail = reloaded.Commands.Skip(reloaded.Commands.Count - direct.Commands.Count).ToList();
        CollectionAssert.AreEqual(
            direct.Commands.Select(c => c.Command.GetType()).ToList(),
            tail.Select(c => c.Command.GetType()).ToList());

        var bytes = reloaded.ToBytes();
        CollectionAssert.AreEqual(Blueline, bytes.Skip(bytes.Length - Blueline.Length).ToArray(),
            "byte tail must stay exact behind the sentinels");
    }

    /// <summary>
    /// Prodigy detection must see through leading CAN+NSR sentinels: a Prodigy stream
    /// reloaded through the non-bare compiler keeps Prodigy metrics (display ratio,
    /// CLUT, MVDI text), so renders match the original file's.
    /// </summary>
    [TestMethod]
    public void ProdigyDetection_SkipsSentinels()
    {
        Assert.AreEqual(NaplpsSystemType.Prodigy, NaplpsFormat.FromBytes(Blueline).SystemType);

        var prefixed = new byte[] { 0x18, 0x1F }.Concat(Blueline).ToArray();
        Assert.AreEqual(NaplpsSystemType.Prodigy, NaplpsFormat.FromBytes(prefixed).SystemType);

        var reloaded = Reload(Blueline, bareFormat: false);
        Assert.AreEqual(NaplpsSystemType.Prodigy, reloaded.SystemType);
    }
}
