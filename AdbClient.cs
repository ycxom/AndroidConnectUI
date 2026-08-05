using System.Diagnostics;

namespace AndroidConnectUI;

/// <summary>Runs adb commands against one device, never throwing at the call site.</summary>
internal sealed class AdbClient(string adbPath, string serial)
{
    public string Serial { get; } = serial;

    public async Task<(string output, string error)> RunAsync(string arguments, int timeoutMs = 3000)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = adbPath;
            process.StartInfo.Arguments = $"-s \"{Serial}\" {arguments}";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            // Drain both redirected pipes while the process is running. Waiting first
            // deadlocks as soon as a dumpsys response fills the OS pipe buffer.
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(timeoutMs);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                try { await process.WaitForExitAsync(); } catch { }
                return ("", "ADB 操作超时");
            }

            return (await outputTask, await errorTask);
        }
        catch (Exception ex)
        {
            return ("", ex.Message);
        }
    }
}
