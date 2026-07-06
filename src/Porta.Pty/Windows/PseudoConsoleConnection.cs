using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Porta.Pty.Windows;

internal sealed class PseudoConsoleConnection : IPtyConnection
{
    private readonly Process _process;
    private readonly Lock _disposeLock = new();
    private PseudoConsoleConnectionHandles? _handles;
    private bool _isDisposed;

    public PseudoConsoleConnection(PseudoConsoleConnectionHandles handles)
    {
        this.ReaderStream = new FileStream(
            new SafeFileHandle(handles.OutPipeOurSide, ownsHandle: false),
            FileAccess.Read,
            bufferSize: 0,
            isAsync: false);
        
        this.WriterStream = new FileStream(
            new SafeFileHandle(handles.InPipeOurSide, ownsHandle: false),
            FileAccess.Write,
            bufferSize: 0,
            isAsync: false);

        this._handles = handles;
        this.Pid = handles.Pid;
        this._process = Process.GetProcessById(this.Pid);
        this._process.Exited += this.Process_Exited;
        this._process.EnableRaisingEvents = true;
    }

    public event EventHandler<PtyExitedEventArgs>? ProcessExited;

    public Stream ReaderStream { get; }

    public Stream WriterStream { get; }

    public int Pid { get; }

    public int ExitCode => this._process.ExitCode;

    public void Dispose()
    {
        lock (this._disposeLock)
        {
            if (this._isDisposed)
            {
                return;
            }

            this._isDisposed = true;
        }

        this._process.Exited -= this.Process_Exited;

        if (this._handles != null)
        {
            if (this._handles.PseudoConsoleHandle != IntPtr.Zero)
            {
                NativeMethods.ClosePseudoConsole(this._handles.PseudoConsoleHandle);
            }

            if (this._handles.InPipeOurSide != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(this._handles.InPipeOurSide);
            }
            if (this._handles.OutPipeOurSide != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(this._handles.OutPipeOurSide);
            }

            if (this._handles.MainThreadHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(this._handles.MainThreadHandle);
            }
            if (this._handles.ProcessHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(this._handles.ProcessHandle);
            }

            if (this._handles.JobObjectHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(this._handles.JobObjectHandle);
            }

            this._handles = null;
        }

        this.ReaderStream?.Dispose();
        this.WriterStream?.Dispose();
        this._process?.Dispose();
    }

    public void Kill()
    {
        this._process.Kill();
    }

    public void Resize(int cols, int rows)
    {
        var localHandles = this._handles;
        if (localHandles != null && !this._isDisposed)
        {
            var coord = new COORD { X = (short)cols, Y = (short)rows };
            var hr = NativeMethods.ResizePseudoConsole(localHandles.PseudoConsoleHandle, coord);
            if (hr < 0)
            {
                throw new InvalidOperationException($"Could not resize pseudo console: HRESULT {hr}");
            }
        }
        else
        {
            throw new ObjectDisposedException(nameof(PseudoConsoleConnection));
        }
    }

    public bool WaitForExit(int milliseconds)
    {
        return this._process.WaitForExit(milliseconds);
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        if (this._isDisposed)
        {
            return;
        }

        this.ProcessExited?.Invoke(this, new PtyExitedEventArgs(this._process.ExitCode));
    }

    internal sealed class PseudoConsoleConnectionHandles(
        IntPtr inPipeOurSide,
        IntPtr outPipeOurSide,
        IntPtr pseudoConsoleHandle,
        IntPtr processHandle,
        int pid,
        IntPtr mainThreadHandle,
        IntPtr jobObjectHandle)
    {
        internal IntPtr InPipeOurSide { get; } = inPipeOurSide;
        internal IntPtr OutPipeOurSide { get; } = outPipeOurSide;
        internal IntPtr PseudoConsoleHandle { get; } = pseudoConsoleHandle;
        internal IntPtr ProcessHandle { get; } = processHandle;
        internal int Pid { get; } = pid;
        internal IntPtr MainThreadHandle { get; } = mainThreadHandle;
        internal IntPtr JobObjectHandle { get; } = jobObjectHandle;
    }
}