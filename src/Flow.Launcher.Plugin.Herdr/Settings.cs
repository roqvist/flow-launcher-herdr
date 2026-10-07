namespace Flow.Launcher.Plugin.Herdr;

/// <summary>
/// Persisted plugin configuration. Loaded/saved via <see cref="IPublicAPI.LoadSettingJsonStorage{T}"/>.
/// </summary>
public class Settings
{
    /// <summary>
    /// Path to the herdr executable. Defaults to "herdr", resolved via PATH.
    /// Override for installs where herdr isn't on PATH (e.g. a specific binary location).
    /// </summary>
    public string HerdrPath { get; set; } = "herdr";
}
