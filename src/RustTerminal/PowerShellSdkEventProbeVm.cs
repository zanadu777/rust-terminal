using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace RustTerminal
{
    internal partial class PowerShellSdkEventProbeVm : ObservableObject
    {
        [ObservableProperty]
        private string eventLogText = string.Empty;

        public ICommand RunCommand { get; }

        public PowerShellSdkEventProbeVm()
        {
            RunCommand = new RelayCommand(async () => await RunProbeAsync());
        }

        private async Task RunProbeAsync()
        {
            EventLogText = string.Empty;
            Append("=== SDK Probe start ===");
            var sw = Stopwatch.StartNew();

            try
            {
                using var runspace = RunspaceFactory.CreateRunspace();

                runspace.StateChanged += (_, e) =>
                {
                    Append($"[Runspace.StateChanged] {e.RunspaceStateInfo.State}");
                    if (e.RunspaceStateInfo.Reason is not null)
                    {
                        Append($"[Runspace.StateChanged.Reason] {e.RunspaceStateInfo.Reason.Message}");
                    }
                };

                Append("[Runspace] Opening");
                runspace.Open();
                Append($"[Runspace] Opened, Availability={runspace.RunspaceAvailability}");

                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                Append($"[PowerShell.Commands.BeforeAdd] Count={ps.Commands.Commands.Count}");

                ps.InvocationStateChanged += (_, e) =>
                {
                    Append($"[InvocationStateChanged] {e.InvocationStateInfo.State}");
                    if (e.InvocationStateInfo.Reason is not null)
                    {
                        Append($"[InvocationStateChanged.Reason] {e.InvocationStateInfo.Reason.Message}");
                    }
                };

                ps.Streams.Error.DataAdded += (_, e) =>
                {
                    var item = ps.Streams.Error[e.Index];
                    Append($"[Error.DataAdded] {item}");
                };

                ps.Streams.Warning.DataAdded += (_, e) =>
                {
                    var item = ps.Streams.Warning[e.Index];
                    Append($"[Warning.DataAdded] {item.Message}");
                };

                ps.Streams.Verbose.DataAdded += (_, e) =>
                {
                    var item = ps.Streams.Verbose[e.Index];
                    Append($"[Verbose.DataAdded] {item.Message}");
                };

                ps.Streams.Debug.DataAdded += (_, e) =>
                {
                    var item = ps.Streams.Debug[e.Index];
                    Append($"[Debug.DataAdded] {item.Message}");
                };

                ps.Streams.Information.DataAdded += (_, e) =>
                {
                    var item = ps.Streams.Information[e.Index];
                    Append($"[Information.DataAdded] {item.MessageData}");
                };

                ps.Streams.Progress.DataAdded += (_, e) =>
                {
                    var item = ps.Streams.Progress[e.Index];
                    Append($"[Progress.DataAdded] ActivityId={item.ActivityId} ParentId={item.ParentActivityId} | {item.Activity} | {item.StatusDescription} | {item.PercentComplete}%");
                };

                var output = new PSDataCollection<PSObject>();
                output.DataAdded += (_, e) =>
                {
                    var item = output[e.Index];
                    Append($"[Output.DataAdded] {item}");
                };

                ps.AddScript(@"
Set-Location -LiteralPath 'D:\Dev\Programming 2026\Rust\iron-pydub\iron_pydub_rust'
cargo build
");

                Append($"[PowerShell.Commands.AfterAdd] Count={ps.Commands.Commands.Count}");
                Append($"[PowerShell.HadErrors.BeforeInvoke] {ps.HadErrors}");
                Append($"[RunspaceAvailability.BeforeInvoke] {runspace.RunspaceAvailability}");

                await Task.Run(() => ps.Invoke(null, output));

                Append($"[PowerShell.HadErrors.AfterInvoke] {ps.HadErrors}");
                Append($"[StreamCounts] Output={output.Count}, Error={ps.Streams.Error.Count}, Warning={ps.Streams.Warning.Count}, Verbose={ps.Streams.Verbose.Count}, Debug={ps.Streams.Debug.Count}, Info={ps.Streams.Information.Count}, Progress={ps.Streams.Progress.Count}");
                Append($"[RunspaceAvailability.AfterInvoke] {runspace.RunspaceAvailability}");

                Append("[Runspace] Closing");
                runspace.Close();
                Append("[Runspace] Closed");
            }
            catch (RuntimeException rex)
            {
                Append($"[RuntimeException] {rex.Message}");
                if (rex.ErrorRecord is not null)
                {
                    Append($"[RuntimeException.ErrorRecord] {rex.ErrorRecord}");
                }
            }
            catch (Exception ex)
            {
                Append($"[Exception] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                sw.Stop();
                Append($"[Elapsed] {sw.ElapsedMilliseconds} ms");
                Append("=== SDK Probe end ===");
            }

            await RunRawProcessProbeAsync();
        }

        private async Task RunRawProcessProbeAsync()
        {
            Append(string.Empty);
            Append("=== Raw Process Probe start (stderr chunk capture) ===");

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoProfile -Command \"Set-Location -LiteralPath 'D:\\Dev\\Programming 2026\\Rust\\iron-pydub\\iron_pydub_rust'; cargo build\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["TERM"] = "xterm-256color";
            psi.Environment["CLICOLOR_FORCE"] = "1";
            psi.Environment["CARGO_TERM_COLOR"] = "always";

            using var p = new Process { StartInfo = psi };
            p.Start();

            var stderr = p.StandardError;
            var buf = new char[512];
            var line = new StringBuilder();

            while (!stderr.EndOfStream)
            {
                var read = await stderr.ReadAsync(buf, 0, buf.Length);
                if (read <= 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    var ch = buf[i];
                    if (ch == '\r')
                    {
                        Append($"[CR-UPDATE] {line}");
                        line.Clear();
                    }
                    else if (ch == '\n')
                    {
                        if (line.Length > 0)
                        {
                            Append($"[LINE] {line}");
                            line.Clear();
                        }
                    }
                    else
                    {
                        line.Append(ch);
                    }
                }
            }

            if (line.Length > 0)
            {
                Append($"[TAIL] {line}");
            }

            await p.WaitForExitAsync();
            Append($"[RawProcess.ExitCode] {p.ExitCode}");
            Append("=== Raw Process Probe end ===");
        }

        private void Append(string line)
        {
            EventLogText += (EventLogText.Length == 0 ? string.Empty : Environment.NewLine) + line;
        }
    }
}
