// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia;

namespace NAPLPSApp.Icons;

public static class MenuItem
{
    public static readonly AttachedProperty<string?> IconProperty =
        AvaloniaProperty.RegisterAttached<Avalonia.Controls.MenuItem, string?>("Icon", typeof(MenuItem));

    static MenuItem()
    {
        IconProperty.Changed.AddClassHandler<Avalonia.Controls.MenuItem>(OnIconChanged);
    }

    public static string? GetIcon(Avalonia.Controls.MenuItem item) => item.GetValue(IconProperty);

    public static void SetIcon(Avalonia.Controls.MenuItem item, string? value) => item.SetValue(IconProperty, value);

    private static void OnIconChanged(Avalonia.Controls.MenuItem item, AvaloniaPropertyChangedEventArgs e)
    {
        var glyph = FontAwesome.Create(e.NewValue as string);

        if (glyph is null)
        {
            return;
        }

        item.Icon = glyph;
    }
}
