// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS;

public struct NaplpsField
{
    public Vector3 Origin { get; set; } = new Vector3(0f, 0f, 0f);

    /// <summary>
    /// Signed field dimensions, exactly as decoded. X3.110 5.3.3.6.2: the dimensions "may be
    /// positive or negative, so that the origin point may be placed in any of the four corners
    /// of the field" - the sign carries which side of the origin the field occupies and must
    /// not be normalized away. Use the edge accessors for extent math.
    /// </summary>
    public Vector3 Dimensions { get; set; } = new Vector3(1f, 1f, 1f);

    public NaplpsField(Vector3 origin, Vector3 dimensions)
    {
        Origin = origin;
        Dimensions = dimensions;
    }

    public readonly float Left => Math.Min(Origin.X, Origin.X + Dimensions.X);

    public readonly float Right => Math.Max(Origin.X, Origin.X + Dimensions.X);

    public readonly float Bottom => Math.Min(Origin.Y, Origin.Y + Dimensions.Y);

    public readonly float Top => Math.Max(Origin.Y, Origin.Y + Dimensions.Y);

    public readonly float Width => Math.Abs(Dimensions.X);

    public readonly float Height => Math.Abs(Dimensions.Y);

    /// <summary>False for the default all-zero field (no FIELD command has run).</summary>
    public readonly bool IsSet => Dimensions.X != 0 || Dimensions.Y != 0;

    public override string ToString()
    {
        // string display this class

        return $"{Origin}, {Dimensions}";
    }
}
