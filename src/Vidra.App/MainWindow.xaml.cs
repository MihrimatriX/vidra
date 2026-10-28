using System;
using System.ComponentModel;
using System.Windows;
using Vidra.App.Services;
using Vidra.App.ViewModels;

namespace Vidra.App;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private bool _exiting;
    private bool _toldAboutTray;

    public MainWindow(MainViewModel vm, AppHost host)
    {
        _host = host;
        DataContext = vm;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
    }

    /// <summary>Kapat düğmesi: ayar açıksa tepsiye küçült (indirmeler sürer), değilse çık.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _host.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                _host.Tray?.Notify("Vidra arka planda çalışıyor", "İndirmeler sürüyor. Tepsi simgesinden açabilir ya da çıkabilirsin.");
            }
            return;
        }
        if (!_exiting)
        {
            e.Cancel = true;
            ExitApp();
        }
        base.OnClosing(e);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Kapat düğmesi de OnClosing'den geçer: tepsiye küçültme ayarı korunur.</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public async void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Hide();
        await ((App)Application.Current).ShutdownGracefullyAsync();
    }
}
