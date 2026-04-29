using System.Diagnostics;

namespace Handoff.WinUI.Services;

/// <summary>
/// Process-wide append-only log for daemon activity. Configure() must be called
/// once at startup with the destination path; before that, Log()/LogError() still
/// emit to Debug.WriteLine but skip the file write — so calls placed deep in the
/// service stack remain safe even when the logger has not yet been wired up.
/// </summary>
public static class Logger
{
    // Single static lock — every Log() call serializes through this so concurrent
    // writes from the polling thread, UI thread, and the Discover button do not
    // interleave and corrupt the file.
    private static readonly object WriteLock = new object();

    private static string? _logPath;

    /// <summary>The file path the logger is currently writing to, or null when unconfigured.</summary>
    public static string? LogPath => _logPath;

    /* ======================================================================================
     * Configure
     * Description: Sets the log file destination, creating its parent directory if
     *              needed. Safe to call multiple times — later calls switch the
     *              destination. Failures are swallowed (logger falls back to Debug
     *              output only) because logging itself must never crash the app.
     * Parameters:
     *   logPath - absolute path to the log file (e.g. <repo>/.local/daemon.log)
     * Return Values: (none)
     * ======================================================================================
     */
    public static void Configure(string logPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _logPath = logPath;
            Log("Logger", "Log file configured at " + logPath);
        }
        catch (Exception ex)
        {
            // Without a usable file path we still get Debug output — better than crashing.
            Debug.WriteLine("[Logger] Configure failed: " + ex.GetType().Name + ": " + ex.Message);
            _logPath = null;
        }
    }

    /* ======================================================================================
     * Log
     * Description: Writes a single timestamped line to the log file (if configured)
     *              AND to Debug.WriteLine. Format:
     *                [yyyy-MM-dd HH:mm:ss.fff] [component] message
     *              Never throws. File-write failures are reported via Debug only —
     *              the calling code keeps running.
     * Parameters:
     *   component - short tag identifying the source (e.g. "SyncService")
     *   message   - the log line; should be a single line (newlines will appear raw)
     * Return Values: (none)
     * ======================================================================================
     */
    public static void Log(string component, string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string line = "[" + timestamp + "] [" + component + "] " + message;

        // Always emit to the debugger output regardless of file state — this preserves
        // the previous Debug.WriteLine behaviour for anyone attached to the debugger.
        Debug.WriteLine(line);

        string? path = _logPath;
        if (path is null)
        {
            return;
        }
        try
        {
            // AppendAllText opens-writes-closes per call. Slightly slower than holding
            // a stream open, but eliminates an entire class of "file locked / not flushed
            // when I tail it" issues that hurt debug ergonomics.
            lock (WriteLock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            // Disk full, permissions, transient lock — none should kill the daemon.
            Debug.WriteLine("[Logger] Write failed (continuing): " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /* ======================================================================================
     * LogError
     * Description: Convenience for exception capture. Produces a single line of the
     *              form "ERROR in <context>: <ExceptionType>: <message>" so failures
     *              are easy to grep for ("ERROR" prefix is the agreed search anchor).
     * Parameters:
     *   component - short tag identifying the source (e.g. "SyncService")
     *   context   - human-readable description of what was being attempted
     *   ex        - the exception that was caught
     * Return Values: (none)
     * ======================================================================================
     */
    public static void LogError(string component, string context, Exception ex)
    {
        Log(component, "ERROR in " + context + ": " + ex.GetType().Name + ": " + ex.Message);
    }
}
