// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS;

public static class Extensions
{
    // Naplps Nuance Extensions

    public static bool IsOpcode(this byte b)
    {
        return (b & 1 << 6) == 0;
    }

    public static bool IsEOF(this BinaryReader stream)
    {
        return stream.BaseStream.IsEOF();
    }

    /// <summary>Bytes still readable from the underlying stream.</summary>
    public static long BytesRemaining(this BinaryReader reader)
    {
        return reader.BaseStream.CanSeek ? reader.BaseStream.Length - reader.BaseStream.Position : 0;
    }

    public static bool IsEOF(this Stream stream)
    {
        return stream.CanSeek && stream.Position >= stream.Length;
    }

    // Stream Extensions
    public static byte PeekByte(this BinaryReader reader)
    {
        // Remember the original position
        long originalPosition = reader.BaseStream.Position;

        if (reader.BaseStream.Position == reader.BaseStream.Length)
        {
            return byte.MinValue;
        }

        // Read the next byte
        byte result = reader.ReadByte();

        // Restore the original position
        reader.BaseStream.Position = originalPosition;

        return result;
    }

    // Color Extensions

    public static ISColor ToISColor(this Color color)
    {
        return ISColor.FromRgba(color.R, color.G, color.B, color.A);
    }

    public static Color From3BitGRB(this Color _, int red3Bit, int green3Bit, int blue3Bit)
    {
        // Ensure the input values are within the 3-bit range
        green3Bit = Math.Clamp(green3Bit, 0, 7);
        red3Bit = Math.Clamp(red3Bit, 0, 7);
        blue3Bit = Math.Clamp(blue3Bit, 0, 7);

        // Convert 3-bit values to 8-bit
        int green = green3Bit * 255 / 7;
        int red = red3Bit * 255 / 7;
        int blue = blue3Bit * 255 / 7;

        // Create and return the new Color
        return Color.FromArgb(255, red, green, blue); // Alpha is set to 255 (fully opaque)
    }

    public static (int green3Bit, int red3Bit, int blue3Bit) To3BitGRB(this Color color)
    {
        // Convert 8-bit values to 3-bit
        int green3Bit = (int)Math.Round(color.G * 7.0 / 255);
        int red3Bit = (int)Math.Round(color.R * 7.0 / 255);
        int blue3Bit = (int)Math.Round(color.B * 7.0 / 255);

        return (green3Bit, red3Bit, blue3Bit);
    }

    public static Color From6BitRGB(this Color _, int red6Bit, int green6Bit, int blue6Bit)
    {
        // Ensure the input values are within the 6-bit range
        red6Bit = Math.Clamp(red6Bit, 0, 63);
        green6Bit = Math.Clamp(green6Bit, 0, 63);
        blue6Bit = Math.Clamp(blue6Bit, 0, 63);

        // Convert 6-bit values to 8-bit
        int red = red6Bit * 255 / 63;
        int green = green6Bit * 255 / 63;
        int blue = blue6Bit * 255 / 63;

        // Create and return the new Color
        return Color.FromArgb(255, red, green, blue); // Alpha is set to 255 (fully opaque)
    }

    public static (int red6Bit, int green6Bit, int blue6Bit) To6BitRGB(this Color color)
    {
        // Convert 8-bit values to 6-bit
        int red6Bit = (int)Math.Round(color.R * 63.0 / 255);
        int green6Bit = (int)Math.Round(color.G * 63.0 / 255);
        int blue6Bit = (int)Math.Round(color.B * 63.0 / 255);

        return (red6Bit, green6Bit, blue6Bit);
    }

    /* Vector Helpers */

    public static Vector3 ToVector3(this Vector2 vec2, float z = 0f)
    {
        return new Vector3(vec2.X, vec2.Y, z);
    }

    public static Vector2 ToVector2(this Vector3 vec3)
    {
        return new Vector2(vec3.X, vec3.Y);
    }
}
