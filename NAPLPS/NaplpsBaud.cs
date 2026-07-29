// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS;

/// <summary>
/// The line speeds a NAPLPS picture could arrive at, and the one canonical list of them.
///
/// NAPLPS drew as its bytes came down the wire, so line speed IS drawing speed - it is a property
/// of the presentation, not a rendering preference. Everything that paces a draw reads from here:
/// the editor's Speed menu, its live canvas, the export dialog, the CLI, and the visual corpus.
/// They used to disagree, which is how the same file could take three seconds in one place and
/// two minutes in another.
/// </summary>
public static class NaplpsBaud
{
    /// <summary>
    /// 1200 bps: the common videotex rate, and what most Prodigy subscribers actually saw.
    /// </summary>
    public const int Default = 1200;

    /// <summary>Draw with no pacing at all - as fast as the renderer can go.</summary>
    public const int Fastest = 0;

    /// <summary>Bits per byte on the wire, being 8N1 framing: 8 data, 1 start, 1 stop.</summary>
    public const double BitsPerByte = 10.0;

    /// <summary>Selectable rates, fastest first.</summary>
    public static readonly int[] Rates =
    [
        Fastest, 460800, 230400, 115200, 57600, 38400, 33600, 28800,
        19200, 14400, 9600, 2400, 1200, 300, 110,
    ];

    /// <summary>Human label for a rate, e.g. "1.2Kbps" or "Fastest".</summary>
    public static string Describe(int rate) => rate switch
    {
        <= 0 => "Fastest",
        >= 1000000 => $"{rate / 1000000.0:0.#}Mbps",
        >= 1000 => $"{rate / 1000.0:0.#}Kbps",
        _ => $"{rate}bps",
    };

    /// <summary>
    /// How long <paramref name="bytes"/> take to arrive at <paramref name="rate"/>, in milliseconds.
    /// Returns 0 when pacing is off.
    /// </summary>
    public static double MillisecondsFor(int bytes, int rate)
    {
        return rate > 0 ? bytes * BitsPerByte * 1000.0 / rate : 0;
    }
}
