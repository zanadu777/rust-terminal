using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PowershellTerminal
{
    /// <summary>
    /// Interaction logic for PowerShellTerminalControl.xaml
    /// </summary>
    public partial class PowerShellTerminalControl : UserControl
    {
        public static readonly DependencyProperty StartingDirectoryProperty =
            DependencyProperty.Register(
                nameof(StartingDirectory),
                typeof(string),
                typeof(PowerShellTerminalControl),
                new PropertyMetadata(null, OnStartingDirectoryChanged));

        private const string InputColorStart = "\u001b[33m";
        private const string InputColorEnd = "\u001b[0m";

        private static readonly Regex ansiSequenceRegex = new(@"(?:\x1B)?\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

        private ConPtyHost? conPtyHost;
        private Process? fallbackProcess;
        private CancellationTokenSource? fallbackReadCts;
        private Task? fallbackStdoutTask;
        private Task? fallbackStderrTask;
        private readonly StringBuilder fallbackInputBuffer = new();
        private readonly List<string> fallbackHistory = new();
        private readonly Queue<string> fallbackEchoSuppressionQueue = new();
        private int fallbackHistoryIndex = -1;
        private string fallbackCurrentDirectory = AppContext.BaseDirectory;
        private string fallbackOutputLineBuffer = string.Empty;
        private readonly StringBuilder conPtyInputBuffer = new();

        private readonly SemaphoreSlim executeCommandLock = new(1, 1);
        private readonly object pendingCommandLock = new();
        private PendingCommandExecution? pendingCommand;
        private string pendingCommandBuffer = string.Empty;

        private bool terminalReady;
        private bool terminalPageReady;
        private bool terminalProcessStarted;
        private TaskCompletionSource<bool>? pageReadyTcs;

        public ExecutionLog ExecutionLog { get; } = new();

        public event EventHandler<CommandExecutionCompletedEventArgs>? CommandCompleted;

        public PowerShellTerminalControl()
        {
            InitializeComponent();
            Loaded += PowerShellTerminalControl_Loaded;
            Unloaded += PowerShellTerminalControl_Unloaded;
            SizeChanged += PowerShellTerminalControl_SizeChanged;
        }

        public string? StartingDirectory
        {
            get => (string?)GetValue(StartingDirectoryProperty);
            set => SetValue(StartingDirectoryProperty, value);
        }

        public Task<CommandExecutionResult> ExecuteCommand(string text)
        {
            return ExecuteCommandCoreAsync(text);
        }

        public Task<IReadOnlyList<CommandExecutionResult>> ExecuteCommand(params string[] texts)
        {
            return ExecuteCommandBatchCoreAsync(texts);
        }

        public Task<IReadOnlyList<CommandExecutionResult>> ExecuteCommand(IEnumerable<string> texts)
        {
            return ExecuteCommandBatchCoreAsync(texts);
        }

        private async Task<IReadOnlyList<CommandExecutionResult>> ExecuteCommandBatchCoreAsync(IEnumerable<string> texts)
        {
            var results = new List<CommandExecutionResult>();
            foreach (var text in texts)
            {
                var result = await ExecuteCommandCoreAsync(text);
                results.Add(result);
            }

            return results;
        }

        private async Task<CommandExecutionResult> ExecuteCommandCoreAsync(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                var now = DateTimeOffset.Now;
                return new CommandExecutionResult(string.Empty, string.Empty, now, now, false);
            }

            var command = text.Trim();

            await executeCommandLock.WaitAsync();
            try
            {
                AddToCommandHistory(command);

                var start = DateTimeOffset.Now;

                if (conPtyHost is null && fallbackProcess is { HasExited: false } && IsClearCommand(command))
                {
                    Write("\u001b[2J\u001b[3J\u001b[H");
                    WriteFallbackPrompt();
                    var clearResult = new CommandExecutionResult(command, string.Empty, start, DateTimeOffset.Now, false);
                    ExecutionLog.Add(clearResult);
                    CommandCompleted?.Invoke(this, new CommandExecutionCompletedEventArgs(clearResult));
                    return clearResult;
                }

                var pending = CreatePendingCommand(command, start, autoPublishOnCompletion: false);

                var wrappedCommand = $"{command}; [Console]::Out.Write('{pending.Token}')";
                SendCommandLineToShell(wrappedCommand, command);

                try
                {
                    await pending.Completion.Task.WaitAsync(TimeSpan.FromMinutes(5));
                }
                catch
                {
                }

                var responseText = string.Empty;
                lock (pendingCommandLock)
                {
                    if (ReferenceEquals(pendingCommand, pending))
                    {
                        responseText = CleanExecutionDetailText(pending.Output.ToString());
                        pendingCommand = null;
                    }

                    pendingCommandBuffer = string.Empty;
                }

                var result = new CommandExecutionResult(command, responseText, start, DateTimeOffset.Now, false);
                ExecutionLog.Add(result);
                CommandCompleted?.Invoke(this, new CommandExecutionCompletedEventArgs(result));
                return result;
            }
            finally
            {
                executeCommandLock.Release();
            }
        }

        private PendingCommandExecution CreatePendingCommand(string command, DateTimeOffset start, bool autoPublishOnCompletion)
        {
            var token = $"__RT_CMD_DONE_{Guid.NewGuid():N}__";
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingCommandExecution(token, completion, command, start, autoPublishOnCompletion);

            lock (pendingCommandLock)
            {
                pendingCommand = pending;
                pendingCommandBuffer = string.Empty;
            }

            return pending;
        }

        private void AddToCommandHistory(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            if (fallbackHistory.Count == 0 || !string.Equals(fallbackHistory[^1], command, StringComparison.Ordinal))
            {
                fallbackHistory.Add(command);
            }

            fallbackHistoryIndex = fallbackHistory.Count;
        }

        private void SendCommandLineToShell(string commandLine, string? displayCommand = null)
        {
            if (conPtyHost is not null)
            {
                conPtyHost.WriteInput(commandLine + "\r");
                return;
            }

            if (fallbackProcess is { HasExited: false })
            {
                var shownCommand = string.IsNullOrWhiteSpace(displayCommand) ? commandLine : displayCommand;
                WriteStyledFallbackCommandLine(shownCommand);
                fallbackEchoSuppressionQueue.Enqueue(commandLine);
                fallbackProcess.StandardInput.WriteLine(commandLine);
                fallbackProcess.StandardInput.Flush();
            }
        }

        private void WriteStyledFallbackCommandLine(string command)
        {
            var firstTokenLength = GetFirstTokenLength(command);
            if (firstTokenLength > 0)
            {
                Write($"{InputColorStart}{command[..firstTokenLength]}{InputColorEnd}{command[firstTokenLength..]}\r\n");
                return;
            }

            Write(command + "\r\n");
        }

        private static int GetFirstTokenLength(string text)
        {
            var i = 0;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            return i;
        }

        private void HandleTerminalOutputChunk(string output)
        {
            var filtered = FilterPendingCommandToken(output);
            if (string.IsNullOrEmpty(filtered))
            {
                PublishPendingCommandIfReady();
                return;
            }

            filtered = FilterFallbackCommandEcho(filtered);
            if (!string.IsNullOrEmpty(filtered))
            {
                AppendPendingCommandOutput(filtered);
                Write(filtered);
            }

            PublishPendingCommandIfReady();
        }

        private void AppendPendingCommandOutput(string chunk)
        {
            lock (pendingCommandLock)
            {
                if (pendingCommand is null)
                {
                    return;
                }

                if (pendingCommand.IsCompleted && !pendingCommand.JustCompleted)
                {
                    return;
                }

                pendingCommand.Output.Append(chunk);
                pendingCommand.JustCompleted = false;
            }
        }

        private void PublishPendingCommandIfReady()
        {
            CommandExecutionResult? result = null;

            lock (pendingCommandLock)
            {
                if (pendingCommand is null || !pendingCommand.IsCompleted || !pendingCommand.AutoPublishOnCompletion)
                {
                    return;
                }

                if (!string.IsNullOrEmpty(fallbackOutputLineBuffer))
                {
                    pendingCommand.Output.Append(fallbackOutputLineBuffer);
                    fallbackOutputLineBuffer = string.Empty;
                }

                result = new CommandExecutionResult(
                    pendingCommand.CommandText,
                    CleanExecutionDetailText(pendingCommand.Output.ToString()),
                    pendingCommand.Start,
                    DateTimeOffset.Now,
                    false);

                pendingCommand = null;
                pendingCommandBuffer = string.Empty;
            }

            ExecutionLog.Add(result);
            CommandCompleted?.Invoke(this, new CommandExecutionCompletedEventArgs(result));
        }

        private static string CleanExecutionDetailText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return ansiSequenceRegex.Replace(text, string.Empty).Trim();
        }

        private string FilterPendingCommandToken(string chunk)
        {
            lock (pendingCommandLock)
            {
                if (string.IsNullOrEmpty(pendingCommand?.Token))
                {
                    pendingCommandBuffer = string.Empty;
                    return chunk;
                }

                if (pendingCommand.IsCompleted)
                {
                    pendingCommandBuffer = string.Empty;
                    return chunk;
                }

                var token = pendingCommand.Token;
                var combined = pendingCommandBuffer + chunk;

                var searchIndex = 0;
                while (true)
                {
                    var tokenIndex = combined.IndexOf(token, searchIndex, StringComparison.Ordinal);
                    if (tokenIndex < 0)
                    {
                        break;
                    }

                    var prevChar = tokenIndex > 0 ? combined[tokenIndex - 1] : '\0';
                    var nextPos = tokenIndex + token.Length;
                    var nextChar = nextPos < combined.Length ? combined[nextPos] : '\0';

                    var looksLikeEchoedQuotedToken = prevChar == '\'' && nextChar == '\'';
                    if (looksLikeEchoedQuotedToken)
                    {
                        searchIndex = tokenIndex + token.Length;
                        continue;
                    }

                    combined = combined.Remove(tokenIndex, token.Length);
                    pendingCommand.IsCompleted = true;
                    pendingCommand.JustCompleted = true;
                    pendingCommand.Completion.TrySetResult(true);

                    pendingCommandBuffer = string.Empty;
                    return combined;
                }

                var carrySize = Math.Min(token.Length - 1, combined.Length);
                var emitLength = combined.Length - carrySize;
                if (emitLength <= 0)
                {
                    pendingCommandBuffer = combined;
                    return string.Empty;
                }

                var emit = combined[..emitLength];
                pendingCommandBuffer = combined[emitLength..];
                return emit;
            }
        }

        private string FilterFallbackCommandEcho(string chunk)
        {
            if (conPtyHost is not null || fallbackProcess is null)
            {
                return chunk;
            }

            if (fallbackEchoSuppressionQueue.Count == 0)
            {
                if (string.IsNullOrEmpty(fallbackOutputLineBuffer))
                {
                    return chunk;
                }

                var passthrough = fallbackOutputLineBuffer + chunk;
                fallbackOutputLineBuffer = string.Empty;
                return passthrough;
            }

            var output = new StringBuilder();
            fallbackOutputLineBuffer += chunk;

            while (true)
            {
                var newlineIndex = fallbackOutputLineBuffer.IndexOf('\n');
                if (newlineIndex < 0)
                {
                    break;
                }

                var lineWithNewLine = fallbackOutputLineBuffer[..(newlineIndex + 1)];
                fallbackOutputLineBuffer = fallbackOutputLineBuffer[(newlineIndex + 1)..];

                var lineWithoutNewLine = lineWithNewLine.TrimEnd('\r', '\n');

                var suppressForPendingToken = !string.IsNullOrEmpty(pendingCommand?.Token)
                                             && lineWithoutNewLine.Contains($"[Console]::Out.Write('{pendingCommand.Token}')", StringComparison.Ordinal);

                if (suppressForPendingToken)
                {
                    if (fallbackEchoSuppressionQueue.Count > 0)
                    {
                        fallbackEchoSuppressionQueue.Dequeue();
                    }

                    continue;
                }

                if (fallbackEchoSuppressionQueue.Count > 0)
                {
                    var expected = fallbackEchoSuppressionQueue.Peek();
                    if (IsFallbackEchoLineMatch(lineWithoutNewLine, expected))
                    {
                        fallbackEchoSuppressionQueue.Dequeue();
                        continue;
                    }

                    fallbackEchoSuppressionQueue.Dequeue();
                }

                output.Append(lineWithNewLine);
            }

            return output.ToString();
        }

        private static bool IsFallbackEchoLineMatch(string line, string expectedCommandLine)
        {
            if (string.Equals(line, expectedCommandLine, StringComparison.Ordinal))
            {
                return true;
            }

            var promptSeparator = "> ";
            var promptIndex = line.LastIndexOf(promptSeparator, StringComparison.Ordinal);
            if (promptIndex >= 0)
            {
                var afterPrompt = line[(promptIndex + promptSeparator.Length)..];
                if (string.Equals(afterPrompt, expectedCommandLine, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private async void PowerShellTerminalControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (conPtyHost is not null || fallbackProcess is { HasExited: false })
            {
                terminalProcessStarted = true;
                return;
            }

            try
            {
                await InitializeTerminalAsync();
                TryStartTerminalIfPossible();
            }
            catch (Exception ex)
            {
                Write($"\r\n[terminal startup error] {ex.Message}\r\n");
            }
        }

        private async Task InitializeTerminalAsync()
        {
            if (terminalReady)
            {
                return;
            }

            await TerminalWebView.EnsureCoreWebView2Async();
            TerminalWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            TerminalWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            TerminalWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            pageReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TerminalWebView.NavigateToString(GetTerminalHtml());

            await pageReadyTcs.Task;
            terminalReady = true;
        }

        private static string GetTerminalHtml()
        {
            return """
<!doctype html>
<html>
<head>
  <meta charset='utf-8'>
  <meta http-equiv='Content-Security-Policy' content="default-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net;">
  <link rel='stylesheet' href='https://cdn.jsdelivr.net/npm/xterm/css/xterm.css' />
  <style>
    html, body, #terminal { height: 100%; width: 100%; margin: 0; background: #1e1f24; }
    .xterm-viewport { scrollbar-color: #4b5263 #1e1f24; }
  </style>
</head>
<body>
  <div id='terminal'></div>
  <script src='https://cdn.jsdelivr.net/npm/xterm/lib/xterm.js'></script>
  <script src='https://cdn.jsdelivr.net/npm/xterm-addon-fit/lib/xterm-addon-fit.js'></script>
  <script>
    const terminal = new Terminal({
      cursorBlink: true,
      cursorStyle: 'block',
      fontFamily: 'JetBrains Mono, Cascadia Mono, Consolas, monospace',
      fontSize: 14,
      letterSpacing: 0,
      lineHeight: 1.1,
      allowProposedApi: true,
      convertEol: true,
      theme: {
        background: '#1e1f24',
        foreground: '#cfd5e0',
        cursor: '#a9b7c6',
        cursorAccent: '#1e1f24',
        selectionBackground: '#2f65ca80',
        black: '#1e1f24',
        red: '#f14c4c',
        green: '#23d18b',
        yellow: '#f5f543',
        blue: '#3b8eea',
        magenta: '#d670d6',
        cyan: '#29b8db',
        white: '#e5e5e5',
        brightBlack: '#666666',
        brightRed: '#f14c4c',
        brightGreen: '#23d18b',
        brightYellow: '#f5f543',
        brightBlue: '#3b8eea',
        brightMagenta: '#d670d6',
        brightCyan: '#29b8db',
        brightWhite: '#e5e5e5'
      },
      scrollback: 20000
    });

    const fitAddon = new FitAddon.FitAddon();
    terminal.loadAddon(fitAddon);

    terminal.open(document.getElementById('terminal'));

    function postMessage(payload) {
      chrome.webview.postMessage(payload);
    }

    document.addEventListener('keydown', (event) => {
      const key = (event.key || '').toLowerCase();
      const ctrlOrCmd = event.ctrlKey || event.metaKey;

      if (ctrlOrCmd && key === 'c') {
        const selection = terminal.getSelection();
        if (selection) {
          postMessage({ type: 'copy', data: selection });
          event.preventDefault();
          event.stopPropagation();
        }
        return;
      }

      if (ctrlOrCmd && event.shiftKey && key === 'c') {
        const selection = terminal.getSelection();
        if (selection) {
          postMessage({ type: 'copy', data: selection });
          event.preventDefault();
          event.stopPropagation();
        }
        return;
      }

      if (ctrlOrCmd && event.shiftKey && key === 'v') {
        postMessage({ type: 'pasteRequest' });
        event.preventDefault();
        event.stopPropagation();
      }
    }, true);

    function sendResize() {
      postMessage({ type: 'resize', cols: terminal.cols, rows: terminal.rows });
    }

    function fitTerminal() {
      fitAddon.fit();
      sendResize();
      terminal.focus();
    }

    window.__fitTerminal = fitTerminal;

    fitTerminal();
    postMessage({ type: 'ready' });

    terminal.onData(data => {
      postMessage({ type: 'input', data });
    });

    chrome.webview.addEventListener('message', event => {
      const message = event.data;
      if (!message || !message.type) return;

      if (message.type === 'write') {
        terminal.write(message.data ?? '');
      } else if (message.type === 'clear') {
        terminal.clear();
      } else if (message.type === 'focus') {
        terminal.focus();
      } else if (message.type === 'paste') {
        terminal.paste(message.data ?? '');
      }
    });

    window.addEventListener('resize', () => fitTerminal());
  </script>
</body>
</html>
""";
        }

        private void StartTerminal(string? startingDirectory)
        {
            var resolvedStartDirectory = ResolveStartingDirectory(startingDirectory);

            if (ShouldUseConPty())
            {
                try
                {
                    conPtyHost = new ConPtyHost();
                    conPtyHost.OutputReceived += OnTerminalOutputReceived;
                    conPtyHost.Start("powershell.exe -NoLogo", resolvedStartDirectory, 120, 30);
                    PostMessageToTerminal(new { type = "focus" });
                    return;
                }
                catch
                {
                    conPtyHost?.Dispose();
                    conPtyHost = null;
                }
            }

            StartFallbackTerminal(resolvedStartDirectory);
        }

        private static bool ShouldUseConPty()
        {
            var value = Environment.GetEnvironmentVariable("RUSTTERMINAL_USE_CONPTY");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        private void StartFallbackTerminal(string workingDirectory)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo",
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.Environment["TERM"] = "xterm-256color";
            startInfo.Environment["CLICOLOR_FORCE"] = "1";
            startInfo.Environment["CARGO_TERM_COLOR"] = "always";
            startInfo.Environment["CARGO_TERM_PROGRESS_WHEN"] = "never";

            fallbackProcess = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            fallbackProcess.Start();

            fallbackReadCts = new CancellationTokenSource();
            fallbackStdoutTask = ReadFallbackStreamAsync(fallbackProcess.StandardOutput, fallbackReadCts.Token);
            fallbackStderrTask = ReadFallbackStreamAsync(fallbackProcess.StandardError, fallbackReadCts.Token);

            fallbackCurrentDirectory = workingDirectory;
            fallbackInputBuffer.Clear();
            fallbackHistoryIndex = fallbackHistory.Count;
            fallbackEchoSuppressionQueue.Clear();
            fallbackOutputLineBuffer = string.Empty;
            conPtyInputBuffer.Clear();
            PostMessageToTerminal(new { type = "focus" });
        }

        private async Task ReadFallbackStreamAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var buffer = new char[1024];

            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                }
                catch
                {
                    break;
                }

                if (read <= 0)
                {
                    break;
                }

                HandleTerminalOutputChunk(new string(buffer, 0, read));
            }
        }

        private static string ResolveStartingDirectory(string? requestedDirectory)
        {
            if (!string.IsNullOrWhiteSpace(requestedDirectory))
            {
                try
                {
                    var fullPath = Path.GetFullPath(requestedDirectory);
                    if (Directory.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
                catch
                {
                }
            }

            return AppContext.BaseDirectory;
        }

        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<TerminalWebMessage>(e.WebMessageAsJson);
                if (payload is null || string.IsNullOrWhiteSpace(payload.type))
                {
                    return;
                }

                switch (payload.type)
                {
                    case "ready":
                        terminalPageReady = true;
                        pageReadyTcs?.TrySetResult(true);
                        break;

                    case "input":
                        if (!string.IsNullOrEmpty(payload.data))
                        {
                            if (conPtyHost is not null)
                            {
                                HandleConPtyInput(payload.data);
                            }
                            else if (fallbackProcess is { HasExited: false })
                            {
                                HandleFallbackInput(payload.data);
                            }
                        }
                        break;

                    case "resize":
                        if (conPtyHost is not null && payload.cols > 0 && payload.rows > 0)
                        {
                            conPtyHost.Resize((short)payload.cols, (short)payload.rows);
                        }
                        break;

                    case "copy":
                        if (!string.IsNullOrEmpty(payload.data))
                        {
                            Clipboard.SetText(payload.data);
                        }
                        break;

                    case "pasteRequest":
                        if (Clipboard.ContainsText())
                        {
                            PostMessageToTerminal(new { type = "paste", data = Clipboard.GetText() });
                        }
                        break;
                }
            }
            catch
            {
            }
        }

        private void HandleFallbackInput(string data)
        {
            if (fallbackProcess is null || fallbackProcess.HasExited)
            {
                return;
            }

            for (var i = 0; i < data.Length; i++)
            {
                var ch = data[i];

                if (ch == '\u001b' && i + 2 < data.Length && data[i + 1] == '[')
                {
                    var code = data[i + 2];
                    i += 2;

                    switch (code)
                    {
                        case 'A':
                            RecallHistoryPrevious();
                            break;
                        case 'B':
                            RecallHistoryNext();
                            break;
                        case 'C':
                        case 'D':
                            break;
                    }

                    continue;
                }

                switch (ch)
                {
                    case '\r':
                        var command = fallbackInputBuffer.ToString();
                        fallbackInputBuffer.Clear();
                        var commandStart = DateTimeOffset.Now;

                        if (!string.IsNullOrWhiteSpace(command))
                        {
                            if (fallbackHistory.Count == 0 || !string.Equals(fallbackHistory[^1], command, StringComparison.Ordinal))
                            {
                                fallbackHistory.Add(command);
                            }
                        }

                        fallbackHistoryIndex = fallbackHistory.Count;

                        if (IsClearCommand(command))
                        {
                            Write("\u001b[2J\u001b[3J\u001b[H");
                            WriteFallbackPrompt();
                            PublishKeyboardCommandResult(command, commandStart);
                            break;
                        }

                        if (!string.IsNullOrWhiteSpace(command))
                        {
                            var trimmedCommand = command.Trim();
                            var pending = CreatePendingCommand(trimmedCommand, commandStart, autoPublishOnCompletion: true);
                            var wrappedCommand = $"{trimmedCommand}; [Console]::Out.Write('{pending.Token}')";
                            fallbackEchoSuppressionQueue.Enqueue(wrappedCommand);
                            Write("\r\n");
                            fallbackProcess.StandardInput.WriteLine(wrappedCommand);
                            fallbackProcess.StandardInput.Flush();
                        }
                        else
                        {
                            Write("\r\n");
                            fallbackProcess.StandardInput.WriteLine(command);
                            fallbackProcess.StandardInput.Flush();
                        }
                        break;

                    case '\u007f':
                    case '\b':
                        if (fallbackInputBuffer.Length > 0)
                        {
                            fallbackInputBuffer.Length--;
                            Write("\b \b");
                        }
                        break;

                    default:
                        if (!char.IsControl(ch))
                        {
                            var isFirstTokenChar = !fallbackInputBuffer.ToString().Contains(' ') && !fallbackInputBuffer.ToString().Contains('\t') && !char.IsWhiteSpace(ch);
                            fallbackInputBuffer.Append(ch);

                            if (isFirstTokenChar)
                            {
                                Write($"{InputColorStart}{ch}{InputColorEnd}");
                            }
                            else
                            {
                                Write(ch.ToString());
                            }
                        }
                        break;
                }
            }
        }

        private void ReplaceCurrentInput(string newInput)
        {
            while (fallbackInputBuffer.Length > 0)
            {
                fallbackInputBuffer.Length--;
                Write("\b \b");
            }

            foreach (var ch in newInput)
            {
                var isFirstTokenChar = !fallbackInputBuffer.ToString().Contains(' ') && !fallbackInputBuffer.ToString().Contains('\t') && !char.IsWhiteSpace(ch);
                fallbackInputBuffer.Append(ch);

                if (isFirstTokenChar)
                {
                    Write($"{InputColorStart}{ch}{InputColorEnd}");
                }
                else
                {
                    Write(ch.ToString());
                }
            }
        }

        private void HandleConPtyInput(string data)
        {
            if (conPtyHost is null)
            {
                return;
            }

            var outbound = new StringBuilder();

            for (var i = 0; i < data.Length; i++)
            {
                var ch = data[i];

                if (ch == '\u007f' || ch == '\b')
                {
                    if (conPtyInputBuffer.Length > 0)
                    {
                        conPtyInputBuffer.Length--;
                    }

                    outbound.Append('\b');
                    continue;
                }

                if (ch == '\r')
                {
                    var command = conPtyInputBuffer.ToString();
                    conPtyInputBuffer.Clear();

                    if (!string.IsNullOrWhiteSpace(command))
                    {
                        var trimmedCommand = command.Trim();
                        var start = DateTimeOffset.Now;

                        if (IsClearCommand(trimmedCommand))
                        {
                            PublishKeyboardCommandResult(trimmedCommand, start);
                        }
                        else
                        {
                            AddToCommandHistory(trimmedCommand);
                            var pending = CreatePendingCommand(trimmedCommand, start, autoPublishOnCompletion: true);
                            outbound.Append($"; [Console]::Out.Write('{pending.Token}')");
                        }
                    }

                    outbound.Append(ch);
                    continue;
                }

                if (ch != '\n' && !char.IsControl(ch))
                {
                    conPtyInputBuffer.Append(ch);
                }

                outbound.Append(ch);
            }

            if (outbound.Length > 0)
            {
                conPtyHost.WriteInput(outbound.ToString());
            }
        }

        private void WriteFallbackPrompt()
        {
            var condaPrefix = Environment.GetEnvironmentVariable("CONDA_PROMPT_MODIFIER") ?? string.Empty;
            Write($"{condaPrefix}PS {fallbackCurrentDirectory}> ");
        }

        private static bool IsClearCommand(string command)
        {
            var trimmed = command.Trim();
            return string.Equals(trimmed, "clear", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(trimmed, "cls", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(trimmed, "clear-host", StringComparison.OrdinalIgnoreCase);
        }

        private void OnTerminalOutputReceived(string output)
        {
            HandleTerminalOutputChunk(output);
        }

        private void Write(string text)
        {
            PostMessageToTerminal(new
            {
                type = "write",
                data = text
            });
        }

        private void PostMessageToTerminal(object payload)
        {
            Dispatcher.Invoke(() =>
            {
                if (!terminalPageReady || TerminalWebView.CoreWebView2 is null)
                {
                    return;
                }

                TerminalWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
            });
        }

        private async void PowerShellTerminalControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!terminalPageReady || TerminalWebView.CoreWebView2 is null)
            {
                return;
            }

            try
            {
                await TerminalWebView.CoreWebView2.ExecuteScriptAsync("window.__fitTerminal && window.__fitTerminal();");
            }
            catch
            {
            }
        }

        private void PowerShellTerminalControl_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (TerminalWebView.CoreWebView2 is not null)
                {
                    TerminalWebView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                }
            }
            catch
            {
            }

            if (conPtyHost is not null)
            {
                conPtyHost.OutputReceived -= OnTerminalOutputReceived;
                conPtyHost.Dispose();
                conPtyHost = null;
            }

            try
            {
                fallbackReadCts?.Cancel();
            }
            catch
            {
            }

            try
            {
                if (fallbackProcess is { HasExited: false })
                {
                    fallbackProcess.StandardInput.WriteLine("exit");
                    fallbackProcess.StandardInput.Flush();
                    fallbackProcess.WaitForExit(500);
                }
            }
            catch
            {
            }
            finally
            {
                fallbackProcess?.Dispose();
                fallbackProcess = null;
            }

            fallbackReadCts?.Dispose();
            fallbackReadCts = null;
            fallbackStdoutTask = null;
            fallbackStderrTask = null;
            fallbackInputBuffer.Clear();
            fallbackHistory.Clear();
            fallbackHistoryIndex = -1;
            fallbackEchoSuppressionQueue.Clear();
            fallbackOutputLineBuffer = string.Empty;
            conPtyInputBuffer.Clear();

            lock (pendingCommandLock)
            {
                pendingCommandBuffer = string.Empty;
                pendingCommand = null;
            }

            pageReadyTcs = null;
            terminalPageReady = false;
            terminalReady = false;
            terminalProcessStarted = false;
        }

        private void RecallHistoryPrevious()
        {
            if (fallbackHistory.Count == 0 || fallbackHistoryIndex <= 0)
            {
                return;
            }

            fallbackHistoryIndex--;
            ReplaceCurrentInput(fallbackHistory[fallbackHistoryIndex]);
        }

        private void RecallHistoryNext()
        {
            if (fallbackHistory.Count == 0)
            {
                return;
            }

            if (fallbackHistoryIndex < fallbackHistory.Count - 1)
            {
                fallbackHistoryIndex++;
                ReplaceCurrentInput(fallbackHistory[fallbackHistoryIndex]);
            }
            else
            {
                fallbackHistoryIndex = fallbackHistory.Count;
                ReplaceCurrentInput(string.Empty);
            }
        }

        private void PublishKeyboardCommandResult(string? command, DateTimeOffset start)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            var trimmedCommand = command.Trim();
            var result = new CommandExecutionResult(trimmedCommand, trimmedCommand, start, DateTimeOffset.Now, false);
            ExecutionLog.Add(result);
            CommandCompleted?.Invoke(this, new CommandExecutionCompletedEventArgs(result));
        }

        private static void OnStartingDirectoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((PowerShellTerminalControl)d).TryStartTerminalIfPossible();
        }

        private void TryStartTerminalIfPossible()
        {
            if (!terminalReady || terminalProcessStarted)
            {
                return;
            }

            if (conPtyHost is not null || fallbackProcess is { HasExited: false })
            {
                terminalProcessStarted = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(StartingDirectory) || !Directory.Exists(StartingDirectory))
            {
                return;
            }

            StartTerminal(StartingDirectory);
            terminalProcessStarted = true;
        }

        private sealed class TerminalWebMessage
        {
            public string? type { get; set; }
            public string? data { get; set; }
            public int cols { get; set; }
            public int rows { get; set; }
        }

        private sealed class PendingCommandExecution
        {
            public string Token { get; }
            public TaskCompletionSource<bool> Completion { get; }
            public string CommandText { get; }
            public DateTimeOffset Start { get; }
            public StringBuilder Output { get; } = new();
            public bool IsCompleted { get; set; }
            public bool JustCompleted { get; set; }
            public bool AutoPublishOnCompletion { get; }

            public PendingCommandExecution(string token, TaskCompletionSource<bool> completion, string commandText, DateTimeOffset start, bool autoPublishOnCompletion)
            {
                Token = token;
                Completion = completion;
                CommandText = commandText;
                Start = start;
                AutoPublishOnCompletion = autoPublishOnCompletion;
            }
        }
    }
}
