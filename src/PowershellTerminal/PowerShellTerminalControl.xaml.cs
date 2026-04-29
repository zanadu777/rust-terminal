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

        private PowerShellHost? shellHost;
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

        public Microsoft.Web.WebView2.Wpf.WebView2? GetWebView2() => TerminalWebView;

        public void SendInterrupt()
        {
            if (shellHost is not null)
            {
                shellHost.WriteInput("\u0003");
            }
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
                var start = DateTimeOffset.Now;

                if (shellHost is null && IsClearCommand(command))
                {
                    Write("\u001b[2J\u001b[3J\u001b[H");
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

        private void SendCommandLineToShell(string commandLine, string? displayCommand = null)
        {
            if (shellHost is not null)
            {
                shellHost.WriteInput(commandLine + "\r");
                return;
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

            AppendPendingCommandOutput(filtered);
            Write(filtered);

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

        private async void PowerShellTerminalControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (shellHost is not null || terminalProcessStarted)
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
                Write($"\r\n[startup error] {ex.Message}\r\n");
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

    let inputLine = '';
    terminal.onData(data => {
      for (let i = 0; i < data.length; i++) {
        const char = data[i];
        
        if (char === '\r') {
          // Enter key - send the complete line ONLY (don't send individual chars)
          // The complete line is what we've buffered + the Enter
          postMessage({ type: 'input', data: inputLine + '\r' });
          inputLine = '';
        } else if (char === '\u007f' || char === '\b') {
          // Backspace - remove from buffer and send to terminal for visual feedback
          if (inputLine.length > 0) {
            inputLine = inputLine.slice(0, -1);
          }
          postMessage({ type: 'input', data: char });
        } else if (char !== '\n' && !char.match(/[\u0000-\u001f]/)) {
          // Regular character - just buffer it, DON'T send to backend yet
          // We'll send it when Enter is pressed
          inputLine += char;
          // But DO write it to the terminal locally for visual feedback
          terminal.write(char);
        }
      }
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

            try
            {
                shellHost = new PowerShellHost();
                shellHost.OutputReceived += OnTerminalOutputReceived;
                shellHost.Start(resolvedStartDirectory);
                PostMessageToTerminal(new { type = "focus" });
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Terminal startup failed: {ex.Message}");
                shellHost?.Dispose();
                shellHost = null;
                
                Write($"[ERROR] Terminal failed to initialize: {ex.Message}\r\n");
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
                            if (shellHost is not null)
                            {
                                HandleConPtyInput(payload.data);
                            }
                        }
                        break;

                    case "resize":
                        if (shellHost is not null && payload.cols > 0 && payload.rows > 0)
                        {
                            shellHost.Resize((short)payload.cols, (short)payload.rows);
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

        private void HandleConPtyInput(string data)
        {
            if (shellHost is null)
            {
                return;
            }

            // Data from JavaScript is either:
            // 1. Backspace character (\b or \u007f)
            // 2. Complete command line ending with \r (only on Enter key)

            for (var i = 0; i < data.Length; i++)
            {
                var ch = data[i];

                // Handle backspace
                if (ch == '\u007f' || ch == '\b')
                {
                    if (conPtyInputBuffer.Length > 0)
                    {
                        conPtyInputBuffer.Length--;
                    }
                    Write("\b \b");
                    continue;
                }

                // Handle Enter - complete line transmission
                if (ch == '\r')
                {
                    var fullCommand = conPtyInputBuffer.ToString();
                    conPtyInputBuffer.Clear();

                    Write("\r\n");
                    
                    // Send the complete command to PowerShell
                    shellHost.WriteInput(fullCommand);
                    continue;
                }

                // Regular character - buffer it (shouldn't normally get here since JS handles echo)
                if (ch != '\n' && !char.IsControl(ch))
                {
                    conPtyInputBuffer.Append(ch);
                }
            }
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

            if (shellHost is not null)
            {
                shellHost.OutputReceived -= OnTerminalOutputReceived;
                shellHost.Dispose();
                shellHost = null;
            }

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

            if (shellHost is not null)
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
