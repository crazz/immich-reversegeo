using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Web.WorkerCommandInvocation;

namespace ImmichReverseGeo.Web.ChildWorkerLaunching;

internal sealed class SystemChildProcessFactory : IChildProcessFactory
{
    public ValueTask<IChildProcess?> StartAsync(ChildProcessStartDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.RedirectStandardInput || !descriptor.RedirectStandardOutput || !descriptor.RedirectStandardError)
        {
            throw new ArgumentException("Child worker streams must be redirected.", nameof(descriptor));
        }

        var process = new Process { StartInfo = CreateStartInfo(descriptor), EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return ValueTask.FromResult<IChildProcess?>(null);
            }

            return ValueTask.FromResult<IChildProcess?>(new SystemChildProcess(process));
        }
        catch
        {
            try
            {
                process.Dispose();
            }
            catch
            {
            }

            throw;
        }
    }

    internal static void ApplyEnvironmentPolicy(
        IDictionary<string, string?> environment,
        ChildProcessEnvironmentPolicy environmentPolicy)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (ChildProcessEnvironmentPolicyDetails.RemovesReservedProtocolVersion(environmentPolicy))
        {
            environment.Remove(ChildProcessEnvironmentPolicyDetails.ReservedProtocolVersionVariable);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(ChildProcessStartDescriptor descriptor)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = descriptor.ExecutablePath,
            WorkingDirectory = descriptor.WorkingDirectory,
            RedirectStandardInput = descriptor.RedirectStandardInput,
            RedirectStandardOutput = descriptor.RedirectStandardOutput,
            RedirectStandardError = descriptor.RedirectStandardError,
            UseShellExecute = descriptor.UseShellExecute,
            CreateNoWindow = descriptor.CreateNoWindow
        };

        foreach (var argument in descriptor.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ApplyEnvironmentPolicy(startInfo.Environment, descriptor.EnvironmentPolicy);
        return startInfo;
    }

    internal sealed class SystemChildProcess : IChildProcess
    {
        private readonly Process _process;
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal SystemChildProcess(Process process)
        {
            _process = process;
            StandardInput = _process.StandardInput.BaseStream;
            StandardOutput = _process.StandardOutput.BaseStream;
            StandardError = _process.StandardError.BaseStream;
            _process.Exited += OnExited;
            if (_process.HasExited)
            {
                OnExited(this, EventArgs.Empty);
            }
        }

        // Borrowed identity for observers; this adapter retains disposal ownership.
        internal Process NativeProcess => _process;

        public int ProcessId => _process.Id;
        public Stream StandardInput { get; }
        public Stream StandardOutput { get; }
        public Stream StandardError { get; }

        public Task<int> WaitForExitAsync() => _exit.Task;

        public ChildProcessExitState GetExitState()
        {
            if (_exit.Task.IsCompletedSuccessfully)
            {
                return ChildProcessExitState.Exited;
            }

            try
            {
                return _process.HasExited ? ChildProcessExitState.Exited : ChildProcessExitState.Alive;
            }
            catch
            {
                return ChildProcessExitState.Unavailable;
            }
        }

        public ChildProcessKillOutcome KillProcessTree()
        {
            if (GetExitState() == ChildProcessExitState.Exited)
            {
                return ChildProcessKillOutcome.AlreadyExited;
            }

            try
            {
                _process.Kill(entireProcessTree: true);
                return ChildProcessKillOutcome.Requested;
            }
            catch (Exception failure)
            {
                // A descendant failure must remain visible even if the root exited.
                if (failure is not AggregateException && GetExitState() == ChildProcessExitState.Exited)
                {
                    return ChildProcessKillOutcome.AlreadyExited;
                }

                return NormalizeKillFailure(failure);
            }
        }

        internal static ChildProcessKillOutcome NormalizeKillFailure(Exception failure)
        {
            return failure switch
            {
                UnauthorizedAccessException or System.Security.SecurityException => ChildProcessKillOutcome.PermissionDenied,
                System.ComponentModel.Win32Exception native when (OperatingSystem.IsWindows()
                    ? native.NativeErrorCode == 5
                    : native.NativeErrorCode is 1 or 13) => ChildProcessKillOutcome.PermissionDenied,
                NotSupportedException => ChildProcessKillOutcome.Unsupported,
                _ => ChildProcessKillOutcome.Failed
            };
        }

        public ValueTask DisposeAsync()
        {
            _process.Exited -= OnExited;
            _process.Dispose();
            return ValueTask.CompletedTask;
        }

        private void OnExited(object? sender, EventArgs args)
        {
            try
            {
                _exit.TrySetResult(_process.ExitCode);
            }
            catch (InvalidOperationException exception)
            {
                _exit.TrySetException(exception);
            }
        }
    }
}
