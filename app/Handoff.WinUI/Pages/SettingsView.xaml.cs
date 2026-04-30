using Handoff.WinUI.Models;
using Handoff.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Handoff.WinUI.Pages;

public sealed partial class SettingsView : UserControl
{
    private ConfigStore? _configStore;
    private bool _suppressThemeEvents;
    private string _initialSupabaseUrl = string.Empty;
    private string _initialSupabaseKey = string.Empty;

    /// <summary>
    /// Raised when the user picks a new theme. The shell applies it to the
    /// window root so the change is visible immediately. The selected value
    /// is one of "System", "Light", "Dark".
    /// </summary>
    public event EventHandler<string>? ThemeChanged;

    public SettingsView()
    {
        this.InitializeComponent();
    }

    public void Bind(ConfigStore configStore, string repoRoot, string configPath, string logPath)
    {
        this._configStore = configStore;
        this.RepoText.Text = repoRoot;
        this.ConfigText.Text = configPath;
        this.LogText.Text = logPath;
        this.LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        if (this._configStore is null)
        {
            return;
        }

        HandoffConfig config = this._configStore.Read();
        this.SelfBox.Text = config.Self ?? string.Empty;
        this._initialSupabaseUrl = config.Supabase?.Url ?? string.Empty;
        this._initialSupabaseKey = config.Supabase?.Key ?? string.Empty;
        this.SupabaseUrlBox.Text = this._initialSupabaseUrl;
        this.SupabaseKeyBox.Password = this._initialSupabaseKey;

        // Avoid firing ThemeChanged while we're populating the radios from disk.
        this._suppressThemeEvents = true;
        string theme = NormalizeTheme(config.Theme);
        this.ThemeSystemRadio.IsChecked = theme == "System";
        this.ThemeLightRadio.IsChecked = theme == "Light";
        this.ThemeDarkRadio.IsChecked = theme == "Dark";
        this._suppressThemeEvents = false;
    }

    private void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (this._suppressThemeEvents || this._configStore is null)
        {
            return;
        }

        string theme = this.ReadSelectedTheme();
        HandoffConfig config = this._configStore.Read();
        config.Theme = theme;
        this._configStore.Write(config);
        this.ThemeChanged?.Invoke(this, theme);
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (this._configStore is null)
        {
            return;
        }

        string newUrl = (this.SupabaseUrlBox.Text ?? string.Empty).Trim();
        string newKey = this.SupabaseKeyBox.Password ?? string.Empty;
        bool supabaseChanged =
            !string.Equals(newUrl, this._initialSupabaseUrl, StringComparison.Ordinal) ||
            !string.Equals(newKey, this._initialSupabaseKey, StringComparison.Ordinal);

        HandoffConfig config = this._configStore.Read();
        config.Self = (this.SelfBox.Text ?? string.Empty).Trim();
        config.Supabase = new SupabaseConfig
        {
            Url = newUrl,
            Key = newKey,
        };
        this._configStore.Write(config);

        this._initialSupabaseUrl = newUrl;
        this._initialSupabaseKey = newKey;

        if (supabaseChanged)
        {
            this.ShowStatus(
                "Saved. Restart required",
                "Supabase URL or key changed. Quit and reopen Handoff so the daemon picks up the new connection.",
                InfoBarSeverity.Warning);
        }
        else
        {
            this.ShowStatus("Saved", "Settings written to config.local.json.", InfoBarSeverity.Success);
        }
    }

    private void OnRevertClicked(object sender, RoutedEventArgs e)
    {
        this.LoadFromConfig();
        this.ShowStatus("Reverted", "Reloaded from config.local.json.", InfoBarSeverity.Informational);
    }

    private string ReadSelectedTheme()
    {
        if (this.ThemeLightRadio.IsChecked == true) return "Light";
        if (this.ThemeDarkRadio.IsChecked == true) return "Dark";
        return "System";
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        this.StatusBar.Title = title;
        this.StatusBar.Message = message;
        this.StatusBar.Severity = severity;
        this.StatusBar.IsOpen = true;
    }

    private static string NormalizeTheme(string? raw)
    {
        return raw switch
        {
            "Light" => "Light",
            "Dark" => "Dark",
            _ => "System",
        };
    }
}
