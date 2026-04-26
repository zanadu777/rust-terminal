using CommunityToolkit.Mvvm.ComponentModel;

namespace RustTerminal;

internal partial class StoredWorkingDirectory : ObservableObject
{
    [ObservableProperty]
    private string directoryPath = string.Empty;

    [ObservableProperty]
    private DateTimeOffset lastUsedUtc;

    public DateTimeOffset LastUsedLocal => LastUsedUtc.ToLocalTime();
}
