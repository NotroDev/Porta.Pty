using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Porta.Pty.Windows;

internal class PtyProvider : IPtyProvider
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    public Task<IPtyConnection> StartTerminalAsync(
        PtyOptions options,
        TraceSource trace,
        CancellationToken cancellationToken)
    {
        return StartPseudoConsoleAsync(options, trace);
    }

    private static string GetAppOnPath(string app, string cwd, IDictionary<string, string> env)
    {
        bool isWow64 = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") != null;
        var windir = Environment.GetEnvironmentVariable("WINDIR") ?? "C:\\Windows";
        var sysnativePath = Path.Combine(windir, "Sysnative");
        var sysnativePathWithSlash = sysnativePath + Path.DirectorySeparatorChar;
        var system32Path = Path.Combine(windir, "System32");
        var system32PathWithSlash = system32Path + Path.DirectorySeparatorChar;

        try
        {
            if (Path.IsPathRooted(app))
            {
                if (isWow64)
                {
                    if (!app.StartsWith(system32PathWithSlash, StringComparison.OrdinalIgnoreCase))
                    {
                        return app;
                    }

                    var sysnativeApp = Path.Combine(sysnativePath, app[system32PathWithSlash.Length..]);
                    if (File.Exists(sysnativeApp))
                    {
                        return sysnativeApp;
                    }
                }
                else if (app.StartsWith(sysnativePathWithSlash, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(system32Path, app[sysnativePathWithSlash.Length..]);
                }

                return app;
            }

            if (Path.GetDirectoryName(app) != string.Empty)
            {
                return Path.Combine(cwd, app);
            }
        }
        catch (ArgumentException)
        {
            throw new ArgumentException($"Invalid terminal app path '{app}'");
        }
        catch (PathTooLongException)
        {
            throw new ArgumentException($"Terminal app path '{app}' is too long");
        }

        string? pathEnvironment = (env != null && env.TryGetValue("PATH", out string? p) ? p : null)
            ?? Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(pathEnvironment))
        {
            return Path.Combine(cwd, app);
        }

        var paths = new List<string>(pathEnvironment.Split([';'], StringSplitOptions.RemoveEmptyEntries));
        if (isWow64)
        {
            var indexOfSystem32 = paths.FindIndex(entry =>
                string.Equals(entry, system32Path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry, system32PathWithSlash, StringComparison.OrdinalIgnoreCase));

            var indexOfSysnative = paths.FindIndex(entry =>
                string.Equals(entry, sysnativePath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry, sysnativePathWithSlash, StringComparison.OrdinalIgnoreCase));

            if (indexOfSystem32 >= 0 && indexOfSysnative == -1)
            {
                paths.Insert(indexOfSystem32, sysnativePath);
            }
        }

        foreach (string pathEntry in paths)
        {
            bool isPathEntryRooted;
            try
            {
                isPathEntryRooted = Path.IsPathRooted(pathEntry);
            }
            catch (ArgumentException)
            {
                continue;
            }

            string fullPath = isPathEntryRooted ? Path.Combine(pathEntry, app) : Path.Combine(cwd, pathEntry, app);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }

            var withExtension = fullPath + ".com";
            if (File.Exists(withExtension))
            {
                return withExtension;
            }

            withExtension = fullPath + ".exe";
            if (File.Exists(withExtension))
            {
                return withExtension;
            }
        }

        return Path.Combine(cwd, app);
    }

    private static string GetEnvironmentString(IDictionary<string, string> environment)
    {
        string[] keys = new string[environment.Count];
        environment.Keys.CopyTo(keys, 0);

        string[] values = new string[environment.Count];
        environment.Values.CopyTo(values, 0);

        Array.Sort(keys, values, StringComparer.OrdinalIgnoreCase);

        var result = new StringBuilder();
        for (int i = 0; i < environment.Count; ++i)
        {
            result.Append(keys[i]);
            result.Append('=');
            result.Append(values[i]);
            result.Append('\0');
        }

        result.Append('\0');
        return result.ToString();
    }

    private Task<IPtyConnection> StartPseudoConsoleAsync(
       PtyOptions options,
       TraceSource trace)
    {
        IntPtr jobObjectHandle = JobObject.Create();
        IntPtr conPtyValuePtr = IntPtr.Zero;
        var startupInfo = new STARTUPINFOEX();

        try
        {
            if (!NativeMethods.CreatePipe(out IntPtr inPipePseudoConsoleSide, out IntPtr inPipeOurSide, IntPtr.Zero, 0) 
                || !NativeMethods.CreatePipe(out IntPtr outPipeOurSide, out IntPtr outPipePseudoConsoleSide, IntPtr.Zero, 0))
            {
                throw new InvalidOperationException("Could not create an anonymous pipe", new Win32Exception());
            }

            var coord = new COORD { X = (short)options.Cols, Y = (short)options.Rows };

            int hr = NativeMethods.CreatePseudoConsole(
                coord,
                inPipePseudoConsoleSide,
                outPipePseudoConsoleSide,
                0,
                out IntPtr pseudoConsoleHandle);

            if (hr < 0)
            {
                throw new InvalidOperationException($"Could not create pseudo console: HRESULT {hr}");
            }

            NativeMethods.CloseHandle(inPipePseudoConsoleSide);
            NativeMethods.CloseHandle(outPipePseudoConsoleSide);

            startupInfo.InitAttributeListAttachedToConPTY(pseudoConsoleHandle);
            
            string app = GetAppOnPath(options.App, options.Cwd, options.Environment);
            string arguments = options.VerbatimCommandLine ?
                WindowsArguments.FormatVerbatim(options.CommandLine) :
                WindowsArguments.Format(options.CommandLine);

            var commandLine = new StringBuilder(app.Length + arguments.Length + 4);
            bool quoteApp = app.Contains(' ') && !app.StartsWith('\"') && !app.EndsWith('\"');
            if (quoteApp)
            {
                commandLine.Append('"').Append(app).Append('"');
            }
            else
            {
                commandLine.Append(app);
            }

            if (!string.IsNullOrWhiteSpace(arguments))
            {
                commandLine.Append(' ');
                commandLine.Append(arguments);
            }

            trace.TraceInformation($"Starting terminal process '{app}' with command line {commandLine}");

            IntPtr processHandle = IntPtr.Zero;
            IntPtr mainThreadHandle = IntPtr.Zero;
            int pid = 0;
            bool success;
            
            string environmentBlock = GetEnvironmentString(options.Environment);
            var environmentHandle = GCHandle.Alloc(Encoding.Unicode.GetBytes(environmentBlock), GCHandleType.Pinned);
            
            IntPtr lpCommandLine = Marshal.StringToHGlobalUni(commandLine.ToString());
            
            try
            {
                success = NativeMethods.CreateProcessW(
                    null,
                    lpCommandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    environmentHandle.AddrOfPinnedObject(),
                    options.Cwd,
                    ref startupInfo,
                    out PROCESS_INFORMATION processInfoRaw);

                if (success)
                {
                    processHandle = processInfoRaw.hProcess;
                    mainThreadHandle = processInfoRaw.hThread;
                    pid = (int)processInfoRaw.dwProcessId;

                    JobObject.AssignProcess(jobObjectHandle, processHandle);
                }
            }
            finally
            {
                environmentHandle.Free();
                
                if (lpCommandLine != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(lpCommandLine);
                }
            }

            if (!success)
            {
                var errorCode = Marshal.GetLastWin32Error();
                var exception = new Win32Exception(errorCode);
                throw new InvalidOperationException($"Could not start terminal process {commandLine}: {exception.Message}", exception);
            }

            var connectionOptions = new PseudoConsoleConnection.PseudoConsoleConnectionHandles(
                inPipeOurSide,
                outPipeOurSide,
                pseudoConsoleHandle,
                processHandle,
                pid,
                mainThreadHandle,
                jobObjectHandle);

            var result = new PseudoConsoleConnection(connectionOptions);
            return Task.FromResult<IPtyConnection>(result);
        }
        catch
        {
            if (jobObjectHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(jobObjectHandle);
            }
            throw;
        }
        finally
        {
            startupInfo.FreeAttributeList();
        }
    }
}