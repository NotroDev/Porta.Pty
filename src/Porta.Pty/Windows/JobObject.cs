using System;
using System.Runtime.InteropServices;

namespace Porta.Pty.Windows;

internal static class JobObject
{
    private const int JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    public static IntPtr Create()
    {
        IntPtr jobHandle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (jobHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create job object", new System.ComponentModel.Win32Exception());
        }

        try
        {
            var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            uint length = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            
            return !NativeMethods.SetInformationJobObject(jobHandle, JOB_OBJECT_EXTENDED_LIMIT_INFORMATION, ref extendedInfo, length) 
                ? throw new InvalidOperationException("Failed to configure job object", new System.ComponentModel.Win32Exception())
                : jobHandle;
        }
        catch
        {
            NativeMethods.CloseHandle(jobHandle);
            throw;
        }
    }

    public static void AssignProcess(IntPtr jobHandle, IntPtr processHandle)
    {
        if (jobHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Invalid job object handle", nameof(jobHandle));
        }

        if (processHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Invalid process handle", nameof(processHandle));
        }

        if (!NativeMethods.AssignProcessToJobObject(jobHandle, processHandle))
        {
            throw new InvalidOperationException("Failed to assign process to job object", new System.ComponentModel.Win32Exception());
        }
    }
}