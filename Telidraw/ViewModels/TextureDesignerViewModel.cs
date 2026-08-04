// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.ViewModels;

/// <summary>
/// Drives the texture designer window. Exposes two 8×8 toggle grids — one for the fill
/// <b>pattern</b>, one for the <b>mask</b> — plus the target mask id (A-D). The OK
/// command produces a payload of 8 pattern bytes + 8 mask bytes suitable for emission
/// by a DEF TEXTURE command.
/// </summary>
public partial class TextureDesignerViewModel : ViewModelBase
{
    public const int GridDim = 8;

    public const int CellCount = GridDim * GridDim;

    public ObservableCollection<DrcsCellViewModel> PatternCells { get; } = [];
    public ObservableCollection<DrcsCellViewModel> MaskCells { get; } = [];

    /// <summary>Mask ID 0..3 (A..D) this texture definition targets.</summary>
    [ObservableProperty]
    private byte maskId;

    public string MaskIdDisplay => $"Mask {(char)('A' + MaskId)}  (ID {MaskId})";

    public bool IsCommitted { get; private set; }

    public TextureDesignerViewModel()
    {
        for (int i = 0; i < CellCount; i++)
        {
            PatternCells.Add(new DrcsCellViewModel { Index = i });
            MaskCells.Add(new DrcsCellViewModel { Index = i });
        }
    }

    partial void OnMaskIdChanged(byte value)
    {
        OnPropertyChanged(nameof(MaskIdDisplay));
    }

    [RelayCommand]
    private void ClearPattern()
    {
        foreach (var c in PatternCells) { c.IsOn = false; }
    }

    [RelayCommand]
    private void ClearMask()
    {
        foreach (var c in MaskCells) { c.IsOn = false; }
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
    /// Pack the pattern grid into 8 operand bytes (bit 7 = leftmost pixel of each row),
    /// wrapped with the 0xC0 numerical-data base. The DEF TEXTURE stream is pattern first
    /// then mask, each 8 rows, preceded by the mask-id character.
    /// </summary>
    public byte[] EncodePattern() => EncodeGrid(PatternCells);

    public byte[] EncodeMask() => EncodeGrid(MaskCells);

    private static byte[] EncodeGrid(IReadOnlyList<DrcsCellViewModel> cells)
    {
        var result = new byte[GridDim];

        for (int row = 0; row < GridDim; row++)
        {
            byte b = 0xC0;

            for (int col = 0; col < Math.Min(6, GridDim); col++)
            {
                if (cells[row * GridDim + col].IsOn)
                {
                    b |= (byte)(1 << (5 - col));
                }
            }

            result[row] = b;
        }

        return result;
    }
}
