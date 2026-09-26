using System.Diagnostics;

namespace ImmichReverseGeo.Tests;

internal sealed class SettingsReaderProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _output;

    private SettingsReaderProcess(Process process)
    {
        _process = process;
        _output = process.StandardOutput.ReadToEndAsync();
    }

    public static async Task<SettingsReaderProcess> StartAsync(string path)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["SETTINGS_TEST_PATH"] = path;
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$f=[IO.File]::Open($env:SETTINGS_TEST_PATH,'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)); $b=New-Object byte[] 100; $n=$f.Read($b,0,100); $o=[Console]::OpenStandardOutput(); $o.Write($b,0,$n); [Console]::Error.WriteLine('ready'); [Console]::ReadLine() | Out-Null; $f.CopyTo($o); $f.Dispose()");
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("exec 3<\"$SETTINGS_TEST_PATH\"; dd bs=1 count=100 <&3 2>/dev/null; echo ready >&2; read release; cat <&3");
        }

        var reader = new SettingsReaderProcess(Process.Start(start)!);
        try
        {
            Assert.AreEqual("ready", await reader._process.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            return reader;
        }
        catch
        {
            await reader.DisposeAsync();
            throw;
        }
    }

    public async Task<string> FinishAsync()
    {
        await _process.StandardInput.WriteLineAsync("release");
        await _process.StandardInput.FlushAsync();
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, _process.ExitCode);
        return await _output;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
    }
}
