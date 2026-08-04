// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia.Input;
using Telidraw.Resources;

namespace NAPLPSTests.Menu;

[TestClass]
public class PlatformGesturesTests
{
    [TestMethod]
    public void Windows_UsesControlModifier()
    {
        var g = PlatformGestureSet.ForPlatform(isMacOS: false);

        AssertGesture(g.Undo,   Key.Z, KeyModifiers.Control);
        AssertGesture(g.Redo,   Key.Y, KeyModifiers.Control);
        AssertGesture(g.Save,   Key.S, KeyModifiers.Control);
        AssertGesture(g.SaveAs, Key.S, KeyModifiers.Control | KeyModifiers.Shift);
    }

    [TestMethod]
    public void MacOS_UsesMetaModifier()
    {
        var g = PlatformGestureSet.ForPlatform(isMacOS: true);

        AssertGesture(g.Undo,   Key.Z, KeyModifiers.Meta);
        AssertGesture(g.Save,   Key.S, KeyModifiers.Meta);
        AssertGesture(g.SaveAs, Key.S, KeyModifiers.Meta | KeyModifiers.Shift);
    }

    [TestMethod]
    public void MacOS_Redo_IsShiftCmdZ_NotCmdY()
    {
        // macOS convention: ⇧⌘Z, not ⌘Y. Non-obvious divergence from Windows.
        var g = PlatformGestureSet.ForPlatform(isMacOS: true);

        AssertGesture(g.Redo, Key.Z, KeyModifiers.Meta | KeyModifiers.Shift);
    }

    [TestMethod]
    public void NoPlatform_UsesControlNotMeta()
    {
        var g = PlatformGestureSet.ForPlatform(isMacOS: false);

        Assert.IsFalse(g.Save.KeyModifiers.HasFlag(KeyModifiers.Meta));
        Assert.IsFalse(g.Undo.KeyModifiers.HasFlag(KeyModifiers.Meta));
    }

    private static void AssertGesture(KeyGesture g, Key key, KeyModifiers modifiers)
    {
        Assert.AreEqual(key, g.Key, $"Key mismatch for {g}");
        Assert.AreEqual(modifiers, g.KeyModifiers, $"Modifier mismatch for {g}");
    }
}
