// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia.Media;

namespace NAPLPSApp.Icons;

public static class FontAwesome
{
    public static readonly FontFamily Solid   = new("avares://NAPLPSApp/Assets/fa7-solid.otf#Font Awesome 7 Free Solid");
    public static readonly FontFamily Regular = new("avares://NAPLPSApp/Assets/fa7-regular.otf#Font Awesome 7 Free");
    public static readonly FontFamily Brand   = new("avares://NAPLPSApp/Assets/fa7-brand.otf#Font Awesome 7 Brands");

    public static readonly IReadOnlyDictionary<string, char> Glyphs = new Dictionary<string, char>
    {
        ["angle-left"]            = '\uf104',
        ["angle-right"]           = '\uf105',
        ["arrow-down"]            = '\uf063',
        ["arrow-left"]            = '\uf060',
        ["arrow-pointer"]         = '\uf245',
        ["arrow-right"]           = '\uf061',
        ["arrow-up"]              = '\uf062',
        ["ban"]                   = '\uf05e',
        ["bezier-curve"]          = '\uf55b',
        ["bookmark"]              = '\uf02e',
        ["border-all"]            = '\uf84c',
        ["bug"]                   = '\uf188',
        ["check"]                 = '\uf00c',
        ["chevron-right"]         = '\uf054',
        ["circle"]                = '\uf111',
        ["circle-check"]          = '\uf058',
        ["circle-info"]           = '\uf05a',
        ["circle-notch"]          = '\uf1ce',
        ["circle-play"]           = '\uf144',
        ["circle-stop"]           = '\uf28d',
        ["circle-xmark"]          = '\uf057',
        ["clone"]                 = '\uf24d',
        ["code"]                  = '\uf121',
        ["copy"]                  = '\uf0c5',
        ["crosshairs"]            = '\uf05b',
        ["display"]               = '\ue163',
        ["door-open"]             = '\uf52b',
        ["draw-polygon"]          = '\uf5ee',
        ["file"]                  = '\uf15b',
        ["file-export"]           = '\uf56e',
        ["file-import"]           = '\uf56f',
        ["fill"]                  = '\uf575',
        ["fill-drip"]             = '\uf576',
        ["film"]                  = '\uf008',
        ["floppy-disk"]           = '\uf0c7',
        ["folder-open"]           = '\uf07c',
        ["font"]                  = '\uf031',
        ["gauge"]                 = '\uf624',
        ["grip"]                  = '\uf58d',
        ["image"]                 = '\uf03e',
        ["keyboard"]              = '\uf11c',
        ["layer-group"]           = '\uf5fd',
        ["magnet"]                = '\uf076',
        ["maximize"]              = '\uf31e',
        ["microchip"]             = '\uf2db',
        ["minus"]                 = '\uf068',
        ["network-wired"]         = '\uf6ff',
        ["paintbrush"]            = '\uf1fc',
        ["palette"]               = '\uf53f',
        ["paper-plane"]           = '\uf1d8',
        ["paste"]                 = '\uf0ea',
        ["pen-nib"]               = '\uf5ad',
        ["pen-ruler"]             = '\uf5ae',
        ["pencil"]                = '\uf303',
        ["plus"]                  = '\uf067',
        ["question"]              = '\uf128',
        ["rectangle-list"]        = '\uf022',
        ["recycle"]               = '\uf1b8',
        ["repeat"]                = '\uf363',
        ["rotate-left"]           = '\uf2ea',
        ["rotate-right"]          = '\uf2f9',
        ["scissors"]              = '\uf0c4',
        ["shoe-prints"]           = '\uf54b',
        ["signature"]             = '\uf5b7',
        ["square"]                = '\uf0c8',
        ["stethoscope"]           = '\uf0f1',
        ["stop"]                  = '\uf04d',
        ["swatchbook"]            = '\uf5c3',
        ["text-width"]            = '\uf035',
        ["trash"]                 = '\uf1f8',
        ["triangle-exclamation"]  = '\uf071',
        ["tv"]                    = '\uf26c',
    };

    public static (FontFamily? Family, char Glyph) Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return (null, '\0');
        }

        var parts = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            return (null, '\0');
        }

        var family = parts[0] switch
        {
            "fa-solid"   => Solid,
            "fa-regular" => Regular,
            "fa-brands"  => Brand,
            _            => null
        };

        var name = parts[1].StartsWith("fa-") ? parts[1][3..] : parts[1];
        Glyphs.TryGetValue(name, out var glyph);

        return (family, glyph);
    }

    public static Avalonia.Controls.TextBlock? Create(string? spec)
    {
        var (family, glyph) = Parse(spec);

        if (family is null || glyph == '\0')
        {
            return null;
        }

        return new Avalonia.Controls.TextBlock
        {
            FontFamily          = family,
            Text                = glyph.ToString(),
            VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };
    }
}
