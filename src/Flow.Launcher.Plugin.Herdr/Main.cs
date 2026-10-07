using System.IO;
using System.Windows.Controls;

namespace Flow.Launcher.Plugin.Herdr;

public class Main : IAsyncPlugin, ISettingProvider, IContextMenu
{
    private const string IconPath = "Icons\\herdr-white.png";

    private PluginInitContext _context = null!;
    private Settings _settings = null!;
    private HerdrClient _client = null!;

    public Task InitAsync(PluginInitContext context)
    {
        _context = context;
        _settings = context.API.LoadSettingJsonStorage<Settings>();
        _client = new HerdrClient(_settings.HerdrPath);
        return Task.CompletedTask;
    }

    public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
    {
        var search = query.Search.TrimStart();

        if (search.StartsWith("a:", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildSendResultAsync(
                prefix: "a:",
                rest: search[2..],
                actionLabel: "Send prompt to",
                hint: "Type a prompt, then press Enter to send",
                send: (session, target, text, t) => _client.SendAgentPromptAsync(session, target, text, t),
                resolveTitle: ResolveAgentTargetTitleAsync,
                token: token).ConfigureAwait(false);
        }

        if (search.StartsWith("p:", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildSendResultAsync(
                prefix: "p:",
                rest: search[2..],
                actionLabel: "Run in pane",
                hint: "Type a command, then press Enter to run",
                send: (session, target, text, t) => _client.RunPaneCommandAsync(session, target, text, t),
                resolveTitle: ResolvePaneTargetTitleAsync,
                token: token).ConfigureAwait(false);
        }

        if (search.StartsWith("r:", StringComparison.OrdinalIgnoreCase))
        {
            return await BuildSendResultAsync(
                prefix: "r:",
                rest: search[2..],
                actionLabel: "Rename pane",
                hint: "Type a new name, then press Enter to rename",
                send: (session, target, text, t) => _client.RenamePaneAsync(session, target, text, t),
                resolveTitle: ResolvePaneTargetTitleAsync,
                token: token).ConfigureAwait(false);
        }

        var sessions = await _client.GetSessionsAsync(token).ConfigureAwait(false);
        var runningSessions = sessions.Where(s => s.Running).ToList();
        if (runningSessions.Count == 0)
        {
            return [];
        }

        token.ThrowIfCancellationRequested();

        var perSession = await Task.WhenAll(runningSessions.Select(s => LoadSessionAsync(s.Name, token)))
            .ConfigureAwait(false);

        var results = new List<Result>();

        foreach (var (agents, panes, workspaceLabels, tabLabels) in perSession)
        {
            var agentPaneIds = agents.Select(a => a.PaneId).ToHashSet(StringComparer.Ordinal);

            foreach (var agent in agents)
            {
                if (MatchesFilter(search, agent.Name, agent.Kind, agent.Cwd, agent.Session))
                {
                    results.Add(BuildAgentResult(agent, workspaceLabels, tabLabels));
                }
            }

            foreach (var pane in panes)
            {
                if (agentPaneIds.Contains(pane.PaneId))
                {
                    continue;
                }

                if (MatchesFilter(search, pane.Title, pane.PaneId, pane.Cwd, pane.Session))
                {
                    results.Add(BuildPaneResult(pane, workspaceLabels, tabLabels));
                }
            }
        }

        return results;
    }

    public Control CreateSettingPanel() => new SettingsControl(_settings, _context.API);

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        return selectedResult.ContextData switch
        {
            AgentContext agent =>
            [
                new Result
                {
                    Title = "Focus agent",
                    SubTitle = $"Bring {agent.Target} to the foreground in Herdr ({agent.Session})",
                    IcoPath = IconPath,
                    Action = ctx =>
                    {
                        _ = _client.FocusAgentAsync(agent.Session, agent.Target, CancellationToken.None);
                        return true;
                    },
                },
                new Result
                {
                    Title = "Copy agent target",
                    SubTitle = agent.Target,
                    IcoPath = IconPath,
                    Action = ctx =>
                    {
                        _context.API.CopyToClipboard(agent.Target);
                        return true;
                    },
                },
            ],
            PaneContext pane =>
            [
                new Result
                {
                    Title = "Focus pane",
                    SubTitle = $"Bring {pane.PaneId} to the foreground in Herdr ({pane.Session})",
                    IcoPath = IconPath,
                    Action = ctx =>
                    {
                        _ = _client.FocusPaneAsync(pane.Session, pane.PaneId, CancellationToken.None);
                        return true;
                    },
                },
                new Result
                {
                    Title = "Copy pane ID",
                    SubTitle = pane.PaneId,
                    IcoPath = IconPath,
                    Action = ctx =>
                    {
                        _context.API.CopyToClipboard(pane.PaneId);
                        return true;
                    },
                },
                new Result
                {
                    Title = "Rename pane",
                    SubTitle = string.IsNullOrEmpty(pane.Label)
                        ? "Set a persistent label for this pane in Herdr"
                        : $"Currently: {pane.Label}",
                    IcoPath = IconPath,
                    Action = ctx =>
                    {
                        var keyword = string.IsNullOrEmpty(_context.CurrentPluginMetadata.ActionKeyword)
                            ? "he"
                            : _context.CurrentPluginMetadata.ActionKeyword;
                        _context.API.ChangeQuery(
                            $"{keyword} r:{pane.Session}/{pane.PaneId} {pane.Label}", true);
                        return false;
                    },
                },
            ],
            _ => [],
        };
    }

    private async Task<(
        IReadOnlyList<HerdrAgent> Agents,
        IReadOnlyList<HerdrPane> Panes,
        IReadOnlyDictionary<string, string> WorkspaceLabels,
        IReadOnlyDictionary<string, string> TabLabels)>
        LoadSessionAsync(string session, CancellationToken token)
    {
        var agentsTask = _client.GetAgentsAsync(session, token);
        var panesTask = _client.GetPanesAsync(session, token);
        var workspaceLabelsTask = _client.GetWorkspaceLabelsAsync(session, token);
        var tabLabelsTask = _client.GetTabLabelsAsync(session, token);
        await Task.WhenAll(agentsTask, panesTask, workspaceLabelsTask, tabLabelsTask).ConfigureAwait(false);
        return (agentsTask.Result, panesTask.Result, workspaceLabelsTask.Result, tabLabelsTask.Result);
    }

    private async Task<List<Result>> BuildSendResultAsync(
        string prefix,
        string rest,
        string actionLabel,
        string hint,
        Func<string, string, string, CancellationToken, Task<HerdrCommandResult>> send,
        Func<string, string, CancellationToken, Task<string>> resolveTitle,
        CancellationToken token)
    {
        var spaceIndex = rest.IndexOf(' ');
        var sessionAndTarget = spaceIndex < 0 ? rest : rest[..spaceIndex];
        var text = spaceIndex < 0 ? string.Empty : rest[(spaceIndex + 1)..].TrimStart();

        var slashIndex = sessionAndTarget.IndexOf('/');
        var session = slashIndex < 0 ? string.Empty : sessionAndTarget[..slashIndex];
        var target = slashIndex < 0 ? sessionAndTarget : sessionAndTarget[(slashIndex + 1)..];

        if (string.IsNullOrEmpty(session) || string.IsNullOrEmpty(target))
        {
            return
            [
                new Result
                {
                    Title = $"Type a target after {prefix}",
                    SubTitle = "Pick an agent or pane from the list first (press Enter on it)",
                    IcoPath = IconPath,
                },
            ];
        }

        var keyword = string.IsNullOrEmpty(_context.CurrentPluginMetadata.ActionKeyword)
            ? "he"
            : _context.CurrentPluginMetadata.ActionKeyword;

        // Resolve the same friendly name (tab label etc.) shown in the top-level list, so this
        // confirmation line matches what the user actually picked instead of a raw pane/agent ID.
        var friendlyTitle = await resolveTitle(session, target, token).ConfigureAwait(false);

        return
        [
            new Result
            {
                Title = $"{actionLabel} {friendlyTitle}",
                SubTitle = string.IsNullOrEmpty(text) ? $"{hint} \u2014 {session}/{target}" : text,
                IcoPath = IconPath,
                Action = ctx =>
                {
                    if (string.IsNullOrEmpty(text))
                    {
                        return false;
                    }

                    _ = SendAndNotifyAsync(session, target, text, send);
                    _context.API.ChangeQuery($"{keyword} ", true);
                    return true;
                },
            },
        ];
    }

    private async Task SendAndNotifyAsync(
        string session,
        string target,
        string text,
        Func<string, string, string, CancellationToken, Task<HerdrCommandResult>> send)
    {
        try
        {
            var result = await send(session, target, text, CancellationToken.None).ConfigureAwait(false);
            if (!result.Success)
            {
                _context.API.ShowMsgError("Herdr command failed", result.Message);
            }
        }
        catch (Exception ex)
        {
            _context.API.LogException(nameof(Main), $"Failed to send to {session}/{target}", ex);
            _context.API.ShowMsgError("Herdr command failed", ex.Message);
        }
    }

    private async Task<string> ResolveAgentTargetTitleAsync(string session, string target, CancellationToken token)
    {
        var agentsTask = _client.GetAgentsAsync(session, token);
        var workspaceLabelsTask = _client.GetWorkspaceLabelsAsync(session, token);
        var tabLabelsTask = _client.GetTabLabelsAsync(session, token);
        await Task.WhenAll(agentsTask, workspaceLabelsTask, tabLabelsTask).ConfigureAwait(false);

        var agent = agentsTask.Result.FirstOrDefault(a =>
            string.Equals(a.Name, target, StringComparison.Ordinal) ||
            string.Equals(a.PaneId, target, StringComparison.Ordinal));

        if (agent is null)
        {
            return target;
        }

        var title = ResolveAgentTitle(agent, workspaceLabelsTask.Result, tabLabelsTask.Result);
        var workspaceLabel = workspaceLabelsTask.Result.GetValueOrDefault(agent.WorkspaceId, agent.WorkspaceId);
        return $"{workspaceLabel} \u203a {title}";
    }

    private async Task<string> ResolvePaneTargetTitleAsync(string session, string target, CancellationToken token)
    {
        var panesTask = _client.GetPanesAsync(session, token);
        var tabLabelsTask = _client.GetTabLabelsAsync(session, token);
        var workspaceLabelsTask = _client.GetWorkspaceLabelsAsync(session, token);
        await Task.WhenAll(panesTask, tabLabelsTask, workspaceLabelsTask).ConfigureAwait(false);

        var pane = panesTask.Result.FirstOrDefault(p => string.Equals(p.PaneId, target, StringComparison.Ordinal));
        if (pane is null)
        {
            return target;
        }

        var title = ResolvePaneTitle(pane, tabLabelsTask.Result);
        var workspaceLabel = workspaceLabelsTask.Result.GetValueOrDefault(pane.WorkspaceId, pane.WorkspaceId);
        return $"{workspaceLabel} \u203a {title}";
    }

    private static string ResolveAgentTitle(
        HerdrAgent agent,
        IReadOnlyDictionary<string, string> workspaceLabels,
        IReadOnlyDictionary<string, string> tabLabels)
    {
        // Agents started via `agent start <name>` have a name; agents herdr auto-detects in an
        // already-running shell (e.g. a plain `omp`/`claude` invocation) do not. For the latter,
        // prefer the tab's own name (what the user actually sees and set themselves in the Herdr
        // TUI, e.g. "CMS", "OMP") over the workspace name, since a workspace can contain several
        // differently-purposed tabs.
        if (!string.IsNullOrEmpty(agent.Name))
        {
            return agent.Name;
        }

        var workspaceLabel = workspaceLabels.GetValueOrDefault(agent.WorkspaceId, agent.WorkspaceId);
        var tabLabel = tabLabels.GetValueOrDefault(agent.TabId, string.Empty);
        return $"{agent.Kind} \u2014 {(string.IsNullOrEmpty(tabLabel) ? workspaceLabel : tabLabel)}";
    }

    private static string ResolvePaneTitle(HerdrPane pane, IReadOnlyDictionary<string, string> tabLabels)
    {
        // Prefer, in order: an explicit pane label (`pane rename`), the tab's own name (what the
        // user actually sees in the Herdr TUI sidebar, e.g. "CMS", "OMP"), then fall back to a
        // shortened terminal title for tabs that were never renamed either.
        if (!string.IsNullOrEmpty(pane.Label))
        {
            return pane.Label;
        }

        var tabLabel = tabLabels.GetValueOrDefault(pane.TabId, string.Empty);
        return !string.IsNullOrEmpty(tabLabel) ? tabLabel : ShortenPaneTitle(pane.Title, pane.Cwd, pane.PaneId);
    }

    private Result BuildAgentResult(
        HerdrAgent agent,
        IReadOnlyDictionary<string, string> workspaceLabels,
        IReadOnlyDictionary<string, string> tabLabels)
    {
        var workspaceLabel = workspaceLabels.GetValueOrDefault(agent.WorkspaceId, agent.WorkspaceId);
        var keyword = string.IsNullOrEmpty(_context.CurrentPluginMetadata.ActionKeyword)
            ? "he"
            : _context.CurrentPluginMetadata.ActionKeyword;
        var target = string.IsNullOrEmpty(agent.Name) ? agent.PaneId : agent.Name;
        var title = ResolveAgentTitle(agent, workspaceLabels, tabLabels);

        return new Result
        {
            Title = title,
            SubTitle = $"{agent.Session} \u2022 {agent.Kind} \u2022 {agent.Status} \u2022 {workspaceLabel}:{agent.PaneId} \u2022 {agent.Cwd}",
            IcoPath = IconPath,
            Score = StatusScore(agent.Status),
            ContextData = new AgentContext(agent.Session, target),
            Action = ctx =>
            {
                _context.API.ChangeQuery($"{keyword} a:{agent.Session}/{target} ", true);
                return false;
            },
        };
    }

    private Result BuildPaneResult(
        HerdrPane pane,
        IReadOnlyDictionary<string, string> workspaceLabels,
        IReadOnlyDictionary<string, string> tabLabels)
    {
        var workspaceLabel = workspaceLabels.GetValueOrDefault(pane.WorkspaceId, pane.WorkspaceId);
        var keyword = string.IsNullOrEmpty(_context.CurrentPluginMetadata.ActionKeyword)
            ? "he"
            : _context.CurrentPluginMetadata.ActionKeyword;
        var title = ResolvePaneTitle(pane, tabLabels);

        return new Result
        {
            Title = title,
            SubTitle = $"{pane.Session} \u2022 {workspaceLabel}:{pane.PaneId} \u2022 {pane.Cwd}",
            IcoPath = IconPath,
            Score = 0,
            ContextData = new PaneContext(pane.Session, pane.PaneId, pane.Label),
            Action = ctx =>
            {
                _context.API.ChangeQuery($"{keyword} p:{pane.Session}/{pane.PaneId} ", true);
                return false;
            },
        };
    }

    private static int StatusScore(string status) => status switch
    {
        "blocked" => 300,
        "working" => 200,
        "idle" or "done" => 100,
        _ => 50,
    };

    private static bool MatchesFilter(string search, params string[] candidates)
    {
        if (string.IsNullOrEmpty(search))
        {
            return true;
        }

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate) &&
                candidate.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ShortenPaneTitle(string title, string cwd, string paneId)
    {
        if (string.IsNullOrEmpty(title))
        {
            return paneId;
        }

        // Raw terminal titles for an unconfigured shell are often a bare executable path (e.g.
        // "C:\...\pwsh.exe"), which is unreadable in a launcher result and, worse, identical
        // across every plain shell pane (see screenshot feedback: five "pwsh.exe" rows with no
        // way to tell them apart at a glance). Collapse to the file name and append the cwd's
        // last folder as a discriminator. Only do this when the whole title looks like a bare
        // path (contains a path separator and no whitespace); custom titles set by the
        // shell/app, which may legitimately contain spaces and path-like substrings (e.g. agent
        // status lines), are left untouched.
        var looksLikeBarePath = !title.Contains(' ') && (title.Contains('\\') || title.Contains('/'));
        if (!looksLikeBarePath)
        {
            return title;
        }

        var fileName = Path.GetFileName(title.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(fileName))
        {
            return title;
        }

        var cwdLeaf = string.IsNullOrEmpty(cwd) ? null : Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(cwdLeaf) ? fileName : $"{fileName} \u2014 {cwdLeaf}";
    }

    private sealed record AgentContext(string Session, string Target);

    private sealed record PaneContext(string Session, string PaneId, string Label);
}
