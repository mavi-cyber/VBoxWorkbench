using System.Diagnostics;
using System.Text;

namespace VBoxWorkbench.Core;

public sealed record RunResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Combined => Error.Length == 0 ? Output : Output.Length == 0 ? Error : Output + "\n" + Error;
}

/// <summary>Finds the VBoxManage executable without assuming an install location.</summary>
public static class VBoxLocator
{
    public static string? Find()
    {
        string exe = OperatingSystem.IsWindows() ? "VBoxManage.exe" : "VBoxManage";
        var candidates = new List<string?>
        {
            Combine(Environment.GetEnvironmentVariable("VBOX_MSI_INSTALL_PATH"), exe),
            Combine(Environment.GetEnvironmentVariable("VBOX_INSTALL_PATH"), exe),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            candidates.Add(Combine(dir.Trim('"'), exe));
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Combine(Environment.GetEnvironmentVariable("ProgramFiles"), @"Oracle\VirtualBox\" + exe));
            candidates.Add(Combine(Environment.GetEnvironmentVariable("ProgramW6432"), @"Oracle\VirtualBox\" + exe));
        }
        else
        {
            candidates.AddRange(["/usr/bin/VBoxManage", "/usr/local/bin/VBoxManage",
                "/Applications/VirtualBox.app/Contents/MacOS/VBoxManage", "/opt/VirtualBox/bin/VBoxManage"]);
        }
        return candidates.FirstOrDefault(c => c != null && File.Exists(c));
    }

    private static string? Combine(string? dir, string file) =>
        string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, file);
}

/// <summary>Runs VBoxManage with an argument list. Nothing is passed through a shell.</summary>
public sealed class VBoxRunner(string exePath)
{
    public string ExePath { get; } = exePath;

    /// <summary>Global options from the manual's "General Options": placed before the subcommand on every run.</summary>
    public List<string> GlobalArgs { get; } = [];

    public async Task<RunResult> RunAsync(IEnumerable<string> args, CancellationToken ct = default,
        Action<string>? onLine = null, bool useGlobalArgs = false, string? program = null)
    {
        var psi = new ProcessStartInfo(program ?? ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (program == null) psi.ArgumentList.Add("-q");
        if (useGlobalArgs) foreach (var g in GlobalArgs) psi.ArgumentList.Add(g);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (stdout) stdout.AppendLine(e.Data); onLine?.Invoke(e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (stderr) stderr.AppendLine(e.Data); onLine?.Invoke(e.Data); } };
        try
        {
            p.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new RunResult(-1, "", "Couldn't start " + psi.FileName + ": " + ex.Message);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new RunResult(-2, stdout.ToString(), "Cancelled.");
        }
        p.WaitForExit();
        return new RunResult(p.ExitCode, stdout.ToString().TrimEnd(), stderr.ToString().TrimEnd());
    }
}
