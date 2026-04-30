using System;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Handoff.WinUI;

public partial class App : Application
{
    private Window? _window;
    private TaskbarIcon? _trayIcon;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Closed += (_, _) => DisposeTrayIcon();
        _window.Activate();
        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        MenuFlyout menu = new MenuFlyout();
        MenuFlyoutItem showItem = new MenuFlyoutItem { Text = "Show Handoff" };
        showItem.Click += (_, _) => ShowMainWindow();
        MenuFlyoutItem exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(showItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/Logo.ico")),
            ToolTipText = "Handoff",
            ContextFlyout = menu,
            LeftClickCommand = new RelayCommand(ShowMainWindow),
        };
        _trayIcon.ForceCreate();
    }

    private void ShowMainWindow()
    {
        if (_window?.AppWindow != null)
        {
            _window.AppWindow.Show();
        }
        _window?.Activate();
    }

    private void ExitApp()
    {
        DisposeTrayIcon();
        _window?.Close();
    }

    private void DisposeTrayIcon()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;

        public RelayCommand(Action execute) => _execute = execute;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute();

#pragma warning disable CS0067 // CanExecute never changes for this command
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    }
}
