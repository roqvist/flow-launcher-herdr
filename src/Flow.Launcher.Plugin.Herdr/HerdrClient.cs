using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Flow.Launcher.Plugin.Herdr;

/// <summary>
/// Thin async wrapper around the `herdr` CLI. Every call shells out to the configured binary;
/// herdr itself talks to a local running server over a Unix-domain/named-pipe socket, so these
/// calls are expected to be fast (observed well under 100ms) when the server is up.
///
/// Herdr supports multiple independently-running named sessions (`herdr session list`), each
/// with its own socket. Every API call below targets one session via the `HERDR_SESSION`
/// environment variable; the implicit unnamed session is itself addressable as "default".
/// </summary>
internal sealed class HerdrClient(string herdrPath)
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Lists all known sessions, running or not. Returns an empty list (not an error) when herdr
    /// isn't installed/on PATH or no session has ever been created, per the plugin's
    /// silent-when-down contract.
    /// </summary>
    public async Task<IReadOnlyList<HerdrSession>> GetSessionsAsync(CancellationToken token)
    {
        try
        {
            var result = await RunAsync(["session", "list", "--json"], session: null, ListTimeout, token)
                .ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return [];
            }

            using var document = JsonDocument.Parse(result.Stdout);
            if (!document.RootElement.TryGetProperty("sessions", out var sessionsEl) ||
                sessionsEl.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var sessions = new List<HerdrSession>();
            foreach (var s in sessionsEl.EnumerateArray())
            {
                var name = GetString(s, "name");
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var running = s.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.True;
                sessions.Add(new HerdrSession(name, running));
            }

            return sessions;
        }
        catch (Win32Exception)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<HerdrAgent>> GetAgentsAsync(string session, CancellationToken token)
    {
        var result = await RunAsync(["agent", "list"], session, ListTimeout, token).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return [];
        }

        using var document = JsonDocument.Parse(result.Stdout);
        if (!document.RootElement.TryGetProperty("result", out var resultEl) ||
            !resultEl.TryGetProperty("agents", out var agentsEl) ||
            agentsEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var agents = new List<HerdrAgent>();
        foreach (var a in agentsEl.EnumerateArray())
        {
            agents.Add(new HerdrAgent(
                Session: session,
                Name: GetString(a, "name"),
                Kind: GetString(a, "agent"),
                Status: GetString(a, "agent_status"),
                Cwd: GetString(a, "cwd"),
                PaneId: GetString(a, "pane_id"),
                TabId: GetString(a, "tab_id"),
                WorkspaceId: GetString(a, "workspace_id")));
        }

        return agents;
    }

    public async Task<IReadOnlyList<HerdrPane>> GetPanesAsync(string session, CancellationToken token)
    {
        var result = await RunAsync(["pane", "list"], session, ListTimeout, token).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return [];
        }

        using var document = JsonDocument.Parse(result.Stdout);
        if (!document.RootElement.TryGetProperty("result", out var resultEl) ||
            !resultEl.TryGetProperty("panes", out var panesEl) ||
            panesEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var panes = new List<HerdrPane>();
        foreach (var p in panesEl.EnumerateArray())
        {
            var title = GetString(p, "terminal_title_stripped");
            if (string.IsNullOrEmpty(title))
            {
                title = GetString(p, "terminal_title");
            }

            panes.Add(new HerdrPane(
                Session: session,
                PaneId: GetString(p, "pane_id"),
                TabId: GetString(p, "tab_id"),
                WorkspaceId: GetString(p, "workspace_id"),
                Cwd: GetString(p, "cwd"),
                Title: title,
                Label: GetString(p, "label")));
        }

        return panes;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetWorkspaceLabelsAsync(
        string session,
        CancellationToken token)
    {
        var result = await RunAsync(["workspace", "list"], session, ListTimeout, token).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return new Dictionary<string, string>();
        }

        using var document = JsonDocument.Parse(result.Stdout);
        if (!document.RootElement.TryGetProperty("result", out var resultEl) ||
            !resultEl.TryGetProperty("workspaces", out var workspacesEl) ||
            workspacesEl.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, string>();
        }

        var labels = new Dictionary<string, string>();
        foreach (var w in workspacesEl.EnumerateArray())
        {
            var id = GetString(w, "workspace_id");
            if (!string.IsNullOrEmpty(id))
            {
                labels[id] = GetString(w, "label");
            }
        }

        return labels;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetTabLabelsAsync(
        string session,
        CancellationToken token)
    {
        var result = await RunAsync(["tab", "list"], session, ListTimeout, token).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return new Dictionary<string, string>();
        }

        using var document = JsonDocument.Parse(result.Stdout);
        if (!document.RootElement.TryGetProperty("result", out var resultEl) ||
            !resultEl.TryGetProperty("tabs", out var tabsEl) ||
            tabsEl.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, string>();
        }

        var labels = new Dictionary<string, string>();
        foreach (var t in tabsEl.EnumerateArray())
        {
            var id = GetString(t, "tab_id");
            if (!string.IsNullOrEmpty(id))
            {
                labels[id] = GetString(t, "label");
            }
        }

        return labels;
    }

    public Task<HerdrCommandResult> SendAgentPromptAsync(
        string session, string target, string text, CancellationToken token) =>
        RunCommandAsync(["agent", "prompt", target, text], session, token);

    public Task<HerdrCommandResult> RunPaneCommandAsync(
        string session, string paneId, string command, CancellationToken token) =>
        RunCommandAsync(["pane", "run", paneId, command], session, token);

    public Task<HerdrCommandResult> FocusAgentAsync(string session, string target, CancellationToken token) =>
        RunCommandAsync(["agent", "focus", target], session, token);

    public Task<HerdrCommandResult> FocusPaneAsync(string session, string paneId, CancellationToken token) =>
        RunCommandAsync(["pane", "focus", paneId], session, token);

    public Task<HerdrCommandResult> RenamePaneAsync(
        string session, string paneId, string label, CancellationToken token) =>
        RunCommandAsync(["pane", "rename", paneId, label], session, token);

    private async Task<HerdrCommandResult> RunCommandAsync(string[] args, string session, CancellationToken token)
    {
        try
        {
            var result = await RunAsync(args, session, CommandTimeout, token).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return new HerdrCommandResult(true, "OK");
            }

            return new HerdrCommandResult(false, ExtractErrorMessage(result.Stdout, result.Stderr));
        }
        catch (Win32Exception ex)
        {
            return new HerdrCommandResult(false, ex.Message);
        }
    }

    private static string ExtractErrorMessage(string stdout, string stderr)
    {
        foreach (var candidate in new[] { stderr, stdout })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? candidate.Trim();
                }
            }
            catch (JsonException)
            {
                // Not JSON; fall through to raw text.
            }

            return candidate.Trim();
        }

        return "herdr command failed";
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string[] args,
        string? session,
        TimeSpan timeout,
        CancellationToken token)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

        var startInfo = new ProcessStartInfo(herdrPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrEmpty(session))
        {
            startInfo.EnvironmentVariables["HERDR_SESSION"] = session;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort; nothing more to do if the process already exited or kill fails.
        }
    }
}
