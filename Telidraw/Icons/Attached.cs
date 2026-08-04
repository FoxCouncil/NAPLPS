// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia;
using Avalonia.Controls;

namespace NAPLPSApp.Icons;

public static class Attached
{
    public static readonly AttachedProperty<string?> IconProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Icon", typeof(Attached));

    static Attached()
    {
        IconProperty.Changed.AddClassHandler<Control>(OnIconChanged);
    }

    public static string? GetIcon(Control control) => control.GetValue(IconProperty);

    public static void SetIcon(Control control, string? value) => control.SetValue(IconProperty, value);

    private static void OnIconChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        var glyph = FontAwesome.Create(e.NewValue as string);

        if (glyph is null)
        {
            return;
        }

        if (control is ContentControl contentControl)
        {
            contentControl.Content = glyph;
        }
    }
}
