// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia;
using Avalonia.Controls;

namespace NAPLPSApp.Icons;

public class Icon : TextBlock
{
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Value));

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    static Icon()
    {
        ValueProperty.Changed.AddClassHandler<Icon>((icon, _) => icon.Refresh());
    }

    private void Refresh()
    {
        var (family, glyph) = FontAwesome.Parse(Value);

        if (family is null || glyph == '\0')
        {
            return;
        }

        FontFamily = family;
        Text       = glyph.ToString();
    }
}
