using System.Windows;
using System.Windows.Controls;

namespace Flow.Launcher.Plugin.Herdr;

public partial class SettingsControl : UserControl
{
    private readonly Settings _settings;
    private readonly IPublicAPI _api;

    public SettingsControl(Settings settings, IPublicAPI api)
    {
        _settings = settings;
        _api = api;
        InitializeComponent();
        HerdrPathTextBox.Text = _settings.HerdrPath;
    }

    private void HerdrPathTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var value = HerdrPathTextBox.Text.Trim();
        _settings.HerdrPath = string.IsNullOrEmpty(value) ? "herdr" : value;
        _api.SaveSettingJsonStorage<Settings>();
    }
}
