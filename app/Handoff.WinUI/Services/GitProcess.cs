using System.Diagnostics;

namespace Handoff.WinUI.Services;

/// <summary>
/// Result of a single git invocation.
/// ExitCode == 0  : git ran and succeeded.
/// ExitCode > 0   : git ran and exited non-zero (Stderr explains why).
/// ExitCode == -1 : the process could not be started at all (Stderr has the .NET exception message).
/// </summary>
public sealed record GitResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => this.ExitCode == 0;
}

public sealed class GitProcess
{
    /* ==================================================================================
     * RunAsync
     * Description: Safely invokes "git" with the given args in the given working
     *              directory. Captures stdout, stderr, and exit code into a GitResult.
     *              Does NOT throw on non-zero git exit codes — the caller branches
     *              on result.Success. OperationCanceledException propagates as-is
     *              so the 30s polling loop can break out cleanly on shutdown.
     * Parameters:
     *   workingDirectory - absolute path to the directory git should run in
     *   args             - git command arguments, one per element
     *                      (e.g., ["fetch", "origin", "--quiet"])
     *   ct               - cancellation token; cancels stream reads and process wait
     * Return Values:
     *   GitResult containing ExitCode, full Stdout, and full Stderr.
     *   See GitResult docs for ExitCode semantics (0, >0, -1).
     *===================================================================================
     */
    public async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct = default)
    {
        // ArgumentList (not Arguments string) is critical for safety: each arg is
        // delivered to the OS as a separate token, preventing shell-style splitting
        // or injection (a branch name containing spaces or ';' stays one argument).
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,    // required for stream redirection + headless run
            CreateNoWindow = true,      // never pop a console window (tray-friendly)
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // Build a printable form of the invocation up-front so log lines on success
        // and failure share the same identifier (helps when grepping the log).
        string invocation = "git " + string.Join(' ', args);
        Logger.Log("git", "Invoke: " + invocation + "  (cwd=" + workingDirectory + ")");

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
            {
                // Process.Start returns null only when no new process is started — rare.
                Logger.Log("git", "Process.Start returned null for: " + invocation);
                return new GitResult(-1, string.Empty, "Process.Start returned null");
            }

            // Read stdout and stderr CONCURRENTLY. Sequential reads can deadlock if
            // git fills one pipe's OS buffer (~64KB on Windows) while we are still
            // draining the other.
            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            // Result line — exit code on every call; stderr only when the call
            // failed, so the log stays compact for the steady-state success path.
            if (proc.ExitCode == 0)
            {
                Logger.Log("git", "Exit 0: " + invocation);
            }
            else
            {
                Logger.Log("git", "Exit " + proc.ExitCode + ": " + invocation
                    + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : "  stderr=" + stderr.Trim()));
            }

            return new GitResult(proc.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            // Best-effort kill so we don't leak a still-running git process when
            // the polling loop is shutting down. Wrapped because Kill throws if
            // the process already exited between the cancel and the kill call.
            try
            {
                proc?.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already on a cancellation path — swallow.
            }
            Logger.Log("git", "Cancelled: " + invocation);
            throw;
        }
        catch (Exception ex)
        {
            // Catches Win32Exception (git not on PATH, invalid working dir),
            // IOException (pipe closed early), and other invocation failures.
            // Per the chosen error policy, invocation errors are RETURNED with
            // ExitCode == -1, not thrown — so the polling loop's other steps
            // can keep running this tick.
            Logger.LogError("git", invocation, ex);
            return new GitResult(-1, string.Empty, ex.Message);
        }
        finally
        {
            proc?.Dispose();
        }
    }
}
