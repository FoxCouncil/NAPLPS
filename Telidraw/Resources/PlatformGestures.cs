// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia.Input;

namespace NAPLPSApp.Resources;

public sealed record PlatformGestureSet(
    KeyGesture Undo,
    KeyGesture Redo,
    KeyGesture Save,
    KeyGesture SaveAs)
{
    public static PlatformGestureSet Current { get; } = ForPlatform(OperatingSystem.IsMacOS());

    public static PlatformGestureSet ForPlatform(bool isMacOS) => isMacOS
        ? new PlatformGestureSet(
            Undo:   KeyGesture.Parse("Cmd+Z"),
            Redo:   KeyGesture.Parse("Cmd+Shift+Z"),
            Save:   KeyGesture.Parse("Cmd+S"),
            SaveAs: KeyGesture.Parse("Cmd+Shift+S"))
        : new PlatformGestureSet(
            Undo:   KeyGesture.Parse("Ctrl+Z"),
            Redo:   KeyGesture.Parse("Ctrl+Y"),
            Save:   KeyGesture.Parse("Ctrl+S"),
            SaveAs: KeyGesture.Parse("Ctrl+Shift+S"));
}

public static class PlatformGestures
{
    public static void Register(Application app)
    {
        var set = PlatformGestureSet.Current;
        app.Resources["UndoGesture"]   = set.Undo;
        app.Resources["RedoGesture"]   = set.Redo;
        app.Resources["SaveGesture"]   = set.Save;
        app.Resources["SaveAsGesture"] = set.SaveAs;
    }
}
