using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CoreShift.Services;

/// <summary>
/// Reads and writes the "Maximum processor state" (PROCTHROTTLEMAX) of the active
/// power scheme by driving <c>powercfg.exe</c> silently. Setting this to 99% disables
/// Intel/AMD Turbo Boost (cooler, quieter "Gaming" profile); 100% re-enables it
/// (full-speed "Coding" profile).
/// </summary>
public sealed partial class PowerService
{
    private const string SubGroup = "SUB_PROCESSOR";
    private const string Setting = "PROCTHROTTLEMAX";

    /// <summary>Returns the current AC max-processor-state percentage, or null if it can't be read.</summary>
    public async Task<int?> GetMaxProcessorStateAsync()
    {
        ProcResult r = await RunAsync($"/q SCHEME_CURRENT {SubGroup} {Setting}");
        if (r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.StdOut))
            return null;

        Match m = AcIndexRegex().Match(r.StdOut);
        if (!m.Success)
            m = DcIndexRegex().Match(r.StdOut);

        if (m.Success && TryParseHex(m.Groups[1].Value, out int value))
            return value;

        return null;
    }

    /// <summary>Writes <paramref name="percent"/> to both AC and DC indices and activates the scheme.</summary>
    public async Task<PowerResult> SetMaxProcessorStateAsync(int percent)
    {
        percent = Math.Clamp(percent, 1, 100);

        ProcResult ac = await RunAsync($"/setacvalueindex SCHEME_CURRENT {SubGroup} {Setting} {percent}");
        ProcResult dc = await RunAsync($"/setdcvalueindex SCHEME_CURRENT {SubGroup} {Setting} {percent}");
        ProcResult activate = await RunAsync("/setactive SCHEME_CURRENT");

        if (ac.ExitCode == 0 && activate.ExitCode == 0)
            return PowerResult.Ok();

        string detail = string.Join(" ",
            new[] { ac.StdErr, dc.StdErr, activate.StdErr }
                .Where(s => !string.IsNullOrWhiteSpace(s)))
            .Trim();

        if (string.IsNullOrWhiteSpace(detail))
            detail = "powercfg could not change the setting. Try launching CoreShift as administrator.";

        return PowerResult.Fail(detail);
    }

    private static Task<ProcResult> RunAsync(string arguments) => Task.Run(() =>
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using Process? p = Process.Start(psi);
            if (p is null)
                return new ProcResult(-1, string.Empty, "Failed to start powercfg.");

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return new ProcResult(p.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ProcResult(-1, string.Empty, ex.Message);
        }
    });

    private static bool TryParseHex(string hex, out int value)
    {
        try { value = Convert.ToInt32(hex, 16); return true; }
        catch { value = 0; return false; }
    }

    [GeneratedRegex(@"Current AC Power Setting Index:\s*0x([0-9A-Fa-f]+)")]
    private static partial Regex AcIndexRegex();

    [GeneratedRegex(@"Current DC Power Setting Index:\s*0x([0-9A-Fa-f]+)")]
    private static partial Regex DcIndexRegex();

    private readonly record struct ProcResult(int ExitCode, string StdOut, string StdErr);
}

/// <summary>Outcome of a power-setting write.</summary>
public readonly record struct PowerResult(bool Success, string Message)
{
    public static PowerResult Ok() => new(true, string.Empty);
    public static PowerResult Fail(string message) => new(false, message);
}
