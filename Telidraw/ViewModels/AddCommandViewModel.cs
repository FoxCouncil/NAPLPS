// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.ViewModels;

public partial class AddCommandViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<NaplpsCommandReference> commands = [];

    [ObservableProperty]
    private string? selectedCommand;

    public AddCommandViewModel()
    {
        Commands.Clear();

        // This will be temporary.
        Commands = new ObservableCollection<NaplpsCommandReference>(
            NaplpsState.GeneralPDISet
                .Where(command => command.CommandType != typeof(NumericalDataCommand))
        );

        SelectedCommand = Commands.FirstOrDefault()?.Name;
    }
}
