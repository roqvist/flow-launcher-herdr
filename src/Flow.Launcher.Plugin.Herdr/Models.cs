namespace Flow.Launcher.Plugin.Herdr;

/// <summary>
/// A named, independently running herdr server instance. Mirrors fields observed in
/// `herdr session list --json`. The implicit unnamed session is itself named "default".
/// </summary>
internal sealed record HerdrSession(string Name, bool Running);

/// <summary>
/// A live agent (coding assistant) occupying a pane. Mirrors fields observed in
/// `herdr agent list` / `herdr agent get` JSON output.
/// </summary>
internal sealed record HerdrAgent(
    string Session,
    string Name,
    string Kind,
    string Status,
    string Cwd,
    string PaneId,
    string TabId,
    string WorkspaceId);

/// <summary>
/// A terminal pane. Mirrors fields observed in `herdr pane list` JSON output.
/// </summary>
internal sealed record HerdrPane(
    string Session,
    string PaneId,
    string TabId,
    string WorkspaceId,
    string Cwd,
    string Title,
    string Label);

/// <summary>
/// Result of invoking a herdr CLI subcommand.
/// </summary>
internal readonly record struct HerdrCommandResult(bool Success, string Message);
