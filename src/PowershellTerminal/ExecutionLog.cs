using System.Collections.ObjectModel;

namespace PowershellTerminal;

public sealed class ExecutionLog
{
    private readonly ObservableCollection<CommandExecutionResult> items = new();

    public ReadOnlyObservableCollection<CommandExecutionResult> Items { get; }

    public CommandExecutionResult? Current { get; private set; }

    public ExecutionLog()
    {
        Items = new ReadOnlyObservableCollection<CommandExecutionResult>(items);
    }

    public void Add(CommandExecutionResult result)
    {
        items.Add(result);
        Current = result;
    }
}
