using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PowershellTerminal
{
    internal sealed class PowerShellHost : IDisposable
    {
        private Process? shellProcess;
        private StreamWriter? stdin;
        private CancellationTokenSource? readCts;
        private Task? stdoutTask;
        private Task? stderrTask;
        private bool disposed;
        private string currentDirectory = Environment.CurrentDirectory;

        public event Action<string>? OutputReceived;
        public event Action<string>? ErrorReceived;
        public event Action<string>? PromptUpdated;

        public void Start(string workingDirectory)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(PowerShellHost));

            try
            {
                currentDirectory = workingDirectory;

                var escapedDir = workingDirectory.Replace("'", "''");
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoExit -NoLogo -Command \"Set-Location -LiteralPath '{escapedDir}'\"",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                psi.Environment["TERM"] = "xterm-256color";
                psi.Environment["CLICOLOR_FORCE"] = "1";
                psi.Environment["CARGO_TERM_COLOR"] = "always";

                shellProcess = Process.Start(psi);
                if (shellProcess is null)
                    throw new Exception("Failed to start PowerShell");

                stdin = shellProcess.StandardInput;

                readCts = new CancellationTokenSource();
                stdoutTask = Task.Run(() => ReadStream(shellProcess.StandardOutput, readCts.Token));
                stderrTask = Task.Run(() => ReadStream(shellProcess.StandardError, readCts.Token));
            }
            catch (Exception ex)
            {
                OutputReceived?.Invoke($"[ERROR] Failed to initialize PowerShell: {ex.Message}\r\n");
            }
        }

        public void WriteInput(string input)
        {
            if (disposed || stdin is null || shellProcess?.HasExited != false)
                return;

            if (string.IsNullOrEmpty(input))
                return;

            try
            {
                UpdateCurrentDirectoryIfCd(input);
                stdin.WriteLine(input);
                stdin.Flush();
            }
            catch (Exception ex)
            {
                ErrorReceived?.Invoke($"Error: {ex.Message}\r\n");
            }
        }

        public void WriteRawInput(string input)
        {
            if (disposed || stdin is null || shellProcess?.HasExited != false)
                return;

            if (string.IsNullOrEmpty(input))
                return;

            try
            {
                stdin.Write(input);
                stdin.Flush();
            }
            catch (Exception ex)
            {
                ErrorReceived?.Invoke($"Error: {ex.Message}\r\n");
            }
        }

        public async Task<string> ExecuteCommandAsync(string input, Action<string>? onChunk = null)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var effectiveInput = input.Trim();

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoProfile -Command -",
                WorkingDirectory = currentDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.Environment["TERM"] = "xterm-256color";
            psi.Environment["CLICOLOR_FORCE"] = "1";
            psi.Environment["CARGO_TERM_COLOR"] = "always";

            var sb = new StringBuilder();
            using var p = new Process { StartInfo = psi };
            p.Start();

            await p.StandardInput.WriteLineAsync(effectiveInput);
            p.StandardInput.Close();

            Task readStdout = Task.Run(async () =>
            {
                var buffer = new char[1024];
                int read;
                while ((read = await p.StandardOutput.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    var chunk = new string(buffer, 0, read);
                    sb.Append(chunk);
                    onChunk?.Invoke(chunk);
                }
            });

            Task readStderr = Task.Run(async () =>
            {
                var buffer = new char[1024];
                int read;
                while ((read = await p.StandardError.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    var chunk = new string(buffer, 0, read);
                    sb.Append(chunk);
                    onChunk?.Invoke(chunk);
                }
            });

            await Task.WhenAll(p.WaitForExitAsync(), readStdout, readStderr);

            UpdateCurrentDirectoryIfCd(input);
            return sb.ToString();
        }

        public string CurrentDirectory => currentDirectory;

        private void UpdateCurrentDirectoryIfCd(string input)
        {
            var trimmed = input.Trim();
            if (!trimmed.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var target = trimmed[3..].Trim().Trim('\'', '"');
            if (Path.IsPathRooted(target) && Directory.Exists(target))
            {
                currentDirectory = target;
            }
        }

        private void ReadStream(StreamReader reader, CancellationToken cancellationToken)
        {
            try
            {
                var buffer = new char[1024];
                int charsRead;
                var lineBuffer = new StringBuilder();

                while (!cancellationToken.IsCancellationRequested &&
                       (charsRead = reader.Read(buffer, 0, buffer.Length)) > 0 &&
                       !disposed)
                {
                    var text = new string(buffer, 0, charsRead);

                    for (var i = 0; i < text.Length; i++)
                    {
                        var ch = text[i];
                        if (ch == '\r' || ch == '\n')
                        {
                            var line = lineBuffer.ToString();
                            lineBuffer.Clear();

                            if (line.StartsWith("__RT_PROMPT__:", StringComparison.Ordinal))
                            {
                                var prompt = line.Substring("__RT_PROMPT__:".Length);
                                prompt = prompt.TrimEnd('\r', '\n');
                                if (!prompt.EndsWith("> ", StringComparison.Ordinal))
                                {
                                    if (prompt.EndsWith(">", StringComparison.Ordinal))
                                    {
                                        prompt += " ";
                                    }
                                    else
                                    {
                                        prompt += "> ";
                                    }
                                }
                                PromptUpdated?.Invoke(prompt);
                            }
                            else if (line.Length > 0)
                            {
                                OutputReceived?.Invoke(line + "\r\n");
                            }

                            if (ch == '\n' && (i == 0 || text[i - 1] != '\r'))
                            {
                                // already handled newline boundary
                            }
                        }
                        else
                        {
                            lineBuffer.Append(ch);
                        }
                    }
                }

                if (lineBuffer.Length > 0)
                {
                    var line = lineBuffer.ToString();
                    if (line.StartsWith("__RT_PROMPT__:", StringComparison.Ordinal))
                    {
                        var prompt = line.Substring("__RT_PROMPT__:".Length);
                        prompt = prompt.TrimEnd('\r', '\n');
                        if (!prompt.EndsWith("> ", StringComparison.Ordinal))
                        {
                            if (prompt.EndsWith(">", StringComparison.Ordinal))
                            {
                                prompt += " ";
                            }
                            else
                            {
                                prompt += "> ";
                            }
                        }
                        PromptUpdated?.Invoke(prompt);
                    }
                    else
                    {
                        OutputReceived?.Invoke(line);
                    }
                }
            }
            catch when (disposed)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
            catch (Exception ex)
            {
                if (!disposed)
                    ErrorReceived?.Invoke($"Stream error: {ex.Message}\r\n");
            }
        }

        public void Resize(short columns, short rows)
        {
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;

            try { readCts?.Cancel(); } catch { }
            try { stdin?.Dispose(); } catch { }

            try
            {
                if (shellProcess is not null && !shellProcess.HasExited)
                {
                    shellProcess.Kill(entireProcessTree: true);
                }
            }
            catch { }

            try
            {
                Task.WaitAll(new[] { stdoutTask, stderrTask }.Where(t => t is not null).Cast<Task>().ToArray(), 200);
            }
            catch { }

            try { shellProcess?.Dispose(); } catch { }
            try { readCts?.Dispose(); } catch { }
        }
    }
}
