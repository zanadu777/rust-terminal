using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PowershellTerminal
{
    internal sealed class PowerShellHost : IDisposable
    {
        private Process? shellProcess;
        private StreamWriter? stdin;
        private bool disposed;

        public event Action<string>? OutputReceived;
        public event Action<string>? ErrorReceived;

        public void Start(string workingDirectory)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(PowerShellHost));

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoExit -NoLogo -NoProfile",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                shellProcess = Process.Start(psi);
                if (shellProcess is null)
                    throw new Exception("Failed to start PowerShell");

                stdin = shellProcess.StandardInput;

                // Read stdout asynchronously
                Task.Run(() => ReadStream(shellProcess.StandardOutput));
                Task.Run(() => ReadStream(shellProcess.StandardError));
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
                stdin.WriteLine(input);
                stdin.Flush();
            }
            catch (Exception ex)
            {
                ErrorReceived?.Invoke($"Error: {ex.Message}\r\n");
            }
        }

        private void ReadStream(StreamReader reader)
        {
            try
            {
                var buffer = new char[1024];
                int charsRead;
                
                while ((charsRead = reader.Read(buffer, 0, buffer.Length)) > 0 && !disposed)
                {
                    var text = new string(buffer, 0, charsRead);
                    OutputReceived?.Invoke(text);
                }
            }
            catch when (disposed)
            {
                // Expected when disposing
            }
            catch (Exception ex)
            {
                if (!disposed)
                    ErrorReceived?.Invoke($"Stream error: {ex.Message}\r\n");
            }
        }

        public void Resize(short columns, short rows)
        {
            // Not applicable for Process mode
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            
            try
            {
                stdin?.Dispose();
                
                if (shellProcess is not null && !shellProcess.HasExited)
                {
                    shellProcess.Kill(entireProcessTree: true);
                }
                shellProcess?.Dispose();
            }
            catch { }
        }
    }
}
