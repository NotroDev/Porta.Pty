using System;
using System.Runtime.InteropServices;

namespace Porta.Pty.Windows;

[StructLayout(LayoutKind.Sequential)]
internal struct COORD
{
    public short X;
    public short Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct STARTUPINFOEX
{
    public STARTUPINFO StartupInfo;
    public IntPtr lpAttributeList;
}

[StructLayout(LayoutKind.Sequential)]
internal struct STARTUPINFO
{
    public uint cb;
    public IntPtr lpReserved;
    public IntPtr lpDesktop;
    public IntPtr lpTitle;
    public uint dwX;
    public uint dwY;
    public uint dwXSize;
    public uint dwYSize;
    public uint dwXCountChars;
    public uint dwYCountChars;
    public uint dwFillAttribute;
    public uint dwFlags;
    public ushort wShowWindow;
    public ushort cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROCESS_INFORMATION
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public uint dwProcessId;
    public uint dwThreadId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IO_COUNTERS
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

internal static partial class NativeMethods
{
    public const int S_OK = 0;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x20016;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        uint dwFlags,
        ref nint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr Attribute,
        IntPtr lpValue,
        nint cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [LibraryImport("kernel32.dll")]
    public static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(
        out IntPtr hReadPipe,
        out IntPtr hWritePipe,
        IntPtr lpPipeAttributes,
        uint nSize);

    [LibraryImport("kernel32.dll")]
    public static partial int CreatePseudoConsole(
        COORD size,
        IntPtr hInput,
        IntPtr hOutput,
        uint dwFlags,
        out IntPtr phpc);

    [LibraryImport("kernel32.dll")]
    public static partial int ResizePseudoConsole(IntPtr hPC, COORD size);

    [LibraryImport("kernel32.dll")]
    public static partial void ClosePseudoConsole(IntPtr hPC);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(
        IntPtr hJob,
        int JobObjectInformationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessW(
        string? lpApplicationName,
        IntPtr lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    extension(ref STARTUPINFOEX startupInfo)
    {
         internal void InitAttributeListAttachedToConPTY(IntPtr pseudoConsoleHandle)
        {
            startupInfo.StartupInfo.cb = (uint)Marshal.SizeOf<STARTUPINFOEX>();
            startupInfo.StartupInfo.dwFlags = 0x00000100; // STARTF_USESTDHANDLES

            const int attributeCount = 1;
            nint size = 0;

            InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size);
            if (size == 0)
            {
                throw new InvalidOperationException("Couldn't get size of process attribute list", new System.ComponentModel.Win32Exception());
            }

            startupInfo.lpAttributeList = Marshal.AllocHGlobal((int)size);
            if (startupInfo.lpAttributeList == IntPtr.Zero)
            {
                throw new OutOfMemoryException("Couldn't reserve space for a new process attribute list");
            }

            if (!InitializeProcThreadAttributeList(startupInfo.lpAttributeList, attributeCount, 0, ref size))
            {
                throw new InvalidOperationException("Couldn't create new process attribute list", new System.ComponentModel.Win32Exception());
            }

            bool wasInitialized = UpdateProcThreadAttribute(
                startupInfo.lpAttributeList,
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                pseudoConsoleHandle,
                Marshal.SizeOf<IntPtr>(),
                IntPtr.Zero,
                IntPtr.Zero);

            if (!wasInitialized)
            {
                throw new InvalidOperationException("Couldn't update process attribute list", new System.ComponentModel.Win32Exception());
            }
        }

        internal void FreeAttributeList()
        {
            if (startupInfo.lpAttributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
                Marshal.FreeHGlobal(startupInfo.lpAttributeList);
                startupInfo.lpAttributeList = IntPtr.Zero;
            }
        }
    }
}