// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS.Commands;

/// <summary>
/// INCREMENTAL POINT - deposits a string of color specifications in a raster-sequential
/// manner within the active field, one logical pel per specification (X3.110 5.3.3.6.3).
/// </summary>
[AddCommand(240, "Incremental Point", "Display a color bitmap within the active field, one pel per pixel.", Category = CommandCategory.Incremental, DslKeyword = "bitmap")]
public class IncrementalPointCommand : NaplpsCommand
{
    public static new readonly NaplpsOperandType OperandType = NaplpsOperandType.FixedAndString;

    /// <summary>
    /// The packing counter (1-48): the number of consecutive bits taken from the string
    /// operand to make up a single color specification. 0 or &gt;48 makes the whole command
    /// a null operation (5.3.3.6.3).
    /// </summary>
    public int BitsPerPixel { get; }

    /// <summary>
    /// The decoded pel deposits, in normalized coordinates. Each entry is the drawing point
    /// of one deposit; the pel extends dx right/left and dy up/down from it per the signed
    /// logical pel captured in <see cref="PelSize"/>. The raster walk (row capacity, the
    /// end-of-row byte flush, the row step) depends on the active field, the logical pel,
    /// and the drawing point at execution time, so it is resolved here at parse time where
    /// that state is exact, not at render time.
    /// </summary>
    public List<PelDeposit> Deposits { get; } = new();

    /// <summary>Signed logical pel (dx, dy) at execution time.</summary>
    public Vector2 PelSize { get; }

    /// <summary>Whether the command is valid (packing counter in range 1-48).</summary>
    public new bool IsValid { get; }

    public IncrementalPointCommand(NaplpsState state, byte opcode, NaplpsOperands operands) : base(state, opcode, operands)
    {
        if (operands.Count == 0)
        {
            IsValid = false;
            return;
        }

        // The first, fixed-format operand byte carries the packing counter in its low 6 bits.
        BitsPerPixel = operands[0] & 0x3F;

        if (BitsPerPixel == 0 || BitsPerPixel > 48)
        {
            IsValid = false;
            return;
        }

        IsValid = true;
        PelSize = new Vector2(state.LogicalPel.X, state.LogicalPel.Y);

        if (operands.Count > 1)
        {
            DecodeRaster(state, operands);
        }

        // 5.3.3.6.3 step 3: when the operation terminates, the drawing point is set to the
        // origin of the active field.
        if (state.Field.IsSet)
        {
            state.DrawingPoint = state.Field.Origin;
            state.SyncAfterGraphicsMove();
        }
    }

    /// <summary>
    /// The 5.3.3.6.3 algorithm: deposits start at the current drawing point and advance one
    /// signed pel width per color specification. When any portion of the pel would exceed
    /// the active field in X, the REMAINING BITS OF THE CURRENT STRING BYTE ARE DISCARDED
    /// (interpretation resumes at b6 of the next byte), the drawing point returns to the
    /// opposite X boundary, and it steps one signed pel height in Y. Color specifications
    /// are packed high-order-first across the 6 payload bits of each string byte without
    /// regard to byte boundaries.
    /// </summary>
    private void DecodeRaster(NaplpsState state, NaplpsOperands operands)
    {
        // The default active field is the unit screen (5.3.3.6.2).
        float left = 0f, right = 1f, bottom = 0f, top = 1f;

        if (state.Field.IsSet)
        {
            left = state.Field.Left;
            right = state.Field.Right;
            bottom = state.Field.Bottom;
            top = state.Field.Top;
        }

        float dx = PelSize.X;
        float dy = PelSize.Y;

        if (dx == 0 || dy == 0)
        {
            return;
        }

        float x = state.DrawingPoint.X;

        // Rows walk from the drawing point in the pel's signed Y direction (5.3.3.6.3:
        // "if dy is positive, the drawing point moves up") - the string data is encoded
        // bottom-row-first for an upward pel. Device-verified on the 8197_CHIEF capture.
        float y = state.DrawingPoint.Y;

        // A pel at (x, y) occupies [x, x+dx] and [y, y+dy] with signs; it fits the field
        // when no portion exceeds it. A half-pel epsilon absorbs float accumulation over
        // the hundreds of column steps of a row.
        float epsX = Math.Abs(dx) / 2f;
        bool FitsX(float px) => dx > 0 ? px + dx <= right + epsX : px + dx >= left - epsX;

        // Bit cursor over the string operand's 6-bit payloads, high bit (b6) first.
        int byteIndex = 1;
        int bitInByte = 0; // 0..5, counted from b6 down to b1

        int ReadBit()
        {
            if (byteIndex >= operands.Count)
            {
                return -1;
            }

            int bit = (operands[byteIndex] >> (5 - bitInByte)) & 1;

            if (++bitInByte == 6)
            {
                bitInByte = 0;
                byteIndex++;
            }

            return bit;
        }

        while (true)
        {
            if (!FitsX(x))
            {
                // End of row: discard the rest of the current byte, return to the opposite
                // X boundary, and step one pel height. A Y overflow scrolls the field on a
                // real terminal (5.3.3.6.3 step 3); a static image render holds the row
                // instead, which only matters for content taller than its field.
                if (bitInByte != 0)
                {
                    bitInByte = 0;
                    byteIndex++;
                }

                if (byteIndex >= operands.Count)
                {
                    break;
                }

                x = dx > 0 ? left : right;

                float nextY = y + dy;
                bool fitsY = nextY >= bottom - epsX && nextY + Math.Abs(dy) <= top + epsX;
                if (fitsY)
                {
                    y = nextY;
                }
            }

            int color = 0;
            bool ok = true;

            for (int b = 0; b < BitsPerPixel; b++)
            {
                int bit = ReadBit();

                if (bit < 0)
                {
                    ok = false;
                    break;
                }

                color = (color << 1) | bit;
            }

            if (!ok)
            {
                break;
            }

            Deposits.Add(new PelDeposit { X = x, Y = y, ColorValue = color });
            x += dx;
        }
    }

    public struct PelDeposit
    {
        public float X { get; set; }
        public float Y { get; set; }
        public int ColorValue { get; set; }
    }
}
