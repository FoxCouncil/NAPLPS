// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS.Helpers;

/// <summary>
/// Round-trips a rectangular bool grid (DRCS glyph bitmaps, texture masks) as a JSON array
/// of row strings of '1'/'0' - System.Text.Json has no built-in support for
/// multi-dimensional arrays.
/// </summary>
public class BoolGridJsonConverter : JsonConverter<bool[,]>
{
    public override bool[,] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException();
        }

        var rows = new List<string>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                var height = rows.Count;
                var width = height > 0 ? rows[0].Length : 0;
                var grid = new bool[height, width];

                for (var y = 0; y < height; y++)
                {
                    if (rows[y].Length != width)
                    {
                        throw new JsonException("ragged bool grid");
                    }

                    for (var x = 0; x < width; x++)
                    {
                        grid[y, x] = rows[y][x] == '1';
                    }
                }

                return grid;
            }

            rows.Add(reader.GetString() ?? throw new JsonException());
        }

        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, bool[,] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();

        var height = value.GetLength(0);
        var width = value.GetLength(1);
        var row = new char[width];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                row[x] = value[y, x] ? '1' : '0';
            }

            writer.WriteStringValue(new string(row));
        }

        writer.WriteEndArray();
    }
}
