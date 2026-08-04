// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.ViewModels;

/// <summary>
/// Drives the DRCS designer window. Exposes an 8×10 grid of toggleable cells (80 total,
/// flat <see cref="Cells"/> collection indexed row-major), plus the target slot character.
/// The OK command packs the grid into bytes: 10 bytes, each encoding one row with bit 7 =
/// leftmost pixel. This is the simplified 8×10 form (Phase 10 will add full-PDI DRCS).
/// </summary>
public partial class DrcsDesignerViewModel : ViewModelBase
{
    public const int GridWidth = 8;

    public const int GridHeight = 10;

    public const int CellCount = GridWidth * GridHeight;

    /// <summary>Flat row-major toggle grid. Index = row * GridWidth + col.</summary>
    public ObservableCollection<DrcsCellViewModel> Cells { get; } = [];

    /// <summary>The character slot (0x20-0x7F) this DRCS glyph gets assigned to.</summary>
    [ObservableProperty]
    private char slotCharacter = 'A';

    /// <summary>Human-readable hex display of the slot character for the window title.</summary>
    public string SlotDisplay => $"'{SlotCharacter}' (0x{(byte)SlotCharacter:X2})";

    /// <summary>
    /// True when the user has clicked OK. The host window reads this on close to decide
    /// whether to commit the bitmap to the format.
    /// </summary>
    public bool IsCommitted { get; private set; }

    public DrcsDesignerViewModel()
    {
        for (int i = 0; i < CellCount; i++)
        {
            Cells.Add(new DrcsCellViewModel { Index = i });
        }
    }

    partial void OnSlotCharacterChanged(char value)
    {
        OnPropertyChanged(nameof(SlotDisplay));
    }

    [RelayCommand]
    private void ClearGrid()
    {
        foreach (var cell in Cells)
        {
            cell.IsOn = false;
        }
    }

    [RelayCommand]
    private void InvertGrid()
    {
        foreach (var cell in Cells)
        {
            cell.IsOn = !cell.IsOn;
        }
    }

    [RelayCommand]
    private void Commit(Window host)
    {
        IsCommitted = true;
        host.Close();
    }

    [RelayCommand]
    private static void Cancel(Window host)
    {
        host.Close();
    }

    /// <summary>
    /// Encode the current 8×10 grid as 10 bytes (one per row). Bit 7 = leftmost pixel.
    /// Wrapped with the 0xC0 numerical-data base so the bytes survive round-trip as
    /// operand bytes in a DefDRCS command.
    /// </summary>
    public byte[] EncodeBitmap()
    {
        var result = new byte[GridHeight];

        for (int row = 0; row < GridHeight; row++)
        {
            byte b = 0xC0; // numerical data base

            // 8 pixels → 6 usable bits; the simplified form packs 6 leftmost pels into bits 5..0
            // and drops pels 6-7. Full-PDI DRCS is a Phase 10 item.
            for (int col = 0; col < Math.Min(6, GridWidth); col++)
            {
                if (Cells[row * GridWidth + col].IsOn)
                {
                    b |= (byte)(1 << (5 - col));
                }
            }

            result[row] = b;
        }

        return result;
    }
}

public partial class DrcsCellViewModel : ObservableObject
{
    [ObservableProperty]
    private int index;

    [ObservableProperty]
    private bool isOn;
}
