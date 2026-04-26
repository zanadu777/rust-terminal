namespace PowershellTerminal;

public sealed class CommandExecutionResult
{
    public string Command { get; }
    public string ResponseText { get; }
    public DateTimeOffset Start { get; }
    public DateTimeOffset Stop { get; }
    public TimeSpan Duration => Stop - Start;
    public bool IsError { get; }

    public CommandExecutionResult(string command, string responseText, DateTimeOffset start, DateTimeOffset stop, bool isError)
    {
        Command = command;
        ResponseText = responseText;
        Start = start;
        Stop = stop;
        IsError = isError;
    }
}