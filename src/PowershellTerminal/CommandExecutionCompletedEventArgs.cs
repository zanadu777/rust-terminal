namespace PowershellTerminal;

public sealed class CommandExecutionCompletedEventArgs : EventArgs
{
    public CommandExecutionResult Result { get; }

    public CommandExecutionCompletedEventArgs(CommandExecutionResult result)
    {
        Result = result;
    }
}