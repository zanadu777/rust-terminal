using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PowershellTerminal
{
    internal sealed class ConPtyHost : IDisposable
    {
        private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

        private IntPtr pseudoConsole;
        private IntPtr inputWritePipe;
        private IntPtr outputReadPipe;
        private IntPtr attributeList;
        private IntPtr processHandle;
        private IntPtr threadHandle;
        private CancellationTokenSource? readCancellation;
        private Task? readTask;
        private bool disposed;

        public event Action<string>? OutputReceived;

        public void Start(string commandLine, string workingDirectory, short columns, short rows)
        {
            ThrowIfDisposed();

            CreatePipes(out var inputReadSide, out inputWritePipe);
            CreatePipes(out outputReadPipe, out var outputWriteSide);

            var hr = CreatePseudoConsole(new COORD { X = columns, Y = rows }, inputReadSide, outputWriteSide, 0, out pseudoConsole);
            if (hr != 0)
            {
                throw new Win32Exception(hr, "CreatePseudoConsole failed.");
            }

            CloseHandle(inputReadSide);
            CloseHandle(outputWriteSide);

            var startupInfoEx = InitializeStartupInfoEx();

            try
            {
                if (!CreateProcess(
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        EXTENDED_STARTUPINFO_PRESENT,
                        IntPtr.Zero,
                        workingDirectory,
                        ref startupInfoEx,
                        out var processInformation))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed for ConPTY shell.");
                }

                processHandle = processInformation.hProcess;
                threadHandle = processInformation.hThread;
            }
            finally
            {
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                    attributeList = IntPtr.Zero;
                }
            }

            readCancellation = new CancellationTokenSource();
            readTask = Task.Run(() => ReadOutputLoop(readCancellation.Token), readCancellation.Token);
        }

        public void WriteInput(string data)
        {
            ThrowIfDisposed();
            if (inputWritePipe == IntPtr.Zero || string.IsNullOrEmpty(data))
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(data);
            if (!WriteFile(inputWritePipe, bytes, bytes.Length, out _, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteFile failed writing terminal input.");
            }
        }

        public void Resize(short columns, short rows)
        {
            ThrowIfDisposed();
            if (pseudoConsole == IntPtr.Zero)
            {
                return;
            }

            var hr = ResizePseudoConsole(pseudoConsole, new COORD { X = columns, Y = rows });
            if (hr != 0)
            {
                throw new Win32Exception(hr, "ResizePseudoConsole failed.");
            }
        }

        private STARTUPINFOEX InitializeStartupInfoEx()
        {
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
            }

            var pseudoConsolePtr = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(pseudoConsolePtr, pseudoConsole);
                if (!UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        pseudoConsolePtr,
                        (IntPtr)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pseudoConsolePtr);
            }

            return new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
                lpAttributeList = attributeList
            };
        }

        private void ReadOutputLoop(CancellationToken cancellationToken)
        {
            var decoder = Encoding.UTF8.GetDecoder();
            var bytes = new byte[8192];
            var chars = new char[8192];

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!ReadFile(outputReadPipe, bytes, bytes.Length, out var bytesRead, IntPtr.Zero) || bytesRead <= 0)
                {
                    break;
                }

                var charsRead = decoder.GetChars(bytes, 0, bytesRead, chars, 0, flush: false);
                if (charsRead > 0)
                {
                    OutputReceived?.Invoke(new string(chars, 0, charsRead));
                }
            }
        }

        private static void CreatePipes(out IntPtr readHandle, out IntPtr writeHandle)
        {
            var securityAttributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                bInheritHandle = true,
                lpSecurityDescriptor = IntPtr.Zero
            };

            if (!CreatePipe(out readHandle, out writeHandle, ref securityAttributes, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed.");
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            try
            {
                readCancellation?.Cancel();
            }
            catch
            {
            }

            CloseIfSet(ref inputWritePipe);
            CloseIfSet(ref outputReadPipe);

            try
            {
                readTask?.Wait(250);
            }
            catch
            {
            }

            if (attributeList != IntPtr.Zero)
            {
                try
                {
                    DeleteProcThreadAttributeList(attributeList);
                }
                catch
                {
                }

                Marshal.FreeHGlobal(attributeList);
                attributeList = IntPtr.Zero;
            }

            if (pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(pseudoConsole);
                pseudoConsole = IntPtr.Zero;
            }

            CloseIfSet(ref threadHandle);
            CloseIfSet(ref processHandle);

            readCancellation?.Dispose();
            readCancellation = null;
        }

        private static void CloseIfSet(ref IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            int dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            [In] ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }
    }
}
