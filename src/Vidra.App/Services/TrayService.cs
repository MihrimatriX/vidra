using System;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Vidra.App.Services;

/// <summary>Sistem tepsisi simgesi + baloncuk bildirimleri (WinForms NotifyIcon).</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _window;

    public TrayService(Window window, Action pauseAll, Action resumeAll, Action quit)
    {
        _window = window;
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/vidra.ico"))?.Stream;
        _icon = new Forms.NotifyIcon
        {
            Text = "Vidra",
            Icon = stream is not null ? new System.Drawing.Icon(stream) : System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Items.Add("Vidra'yı aç", null, (_, _) => Show());
        _icon.ContextMenuStrip.Items.Add("Tümünü duraklat", null, (_, _) => pauseAll());
        _icon.ContextMenuStrip.Items.Add("Tümünü sürdür", null, (_, _) => resumeAll());
        _icon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _icon.ContextMenuStrip.Items.Add("Çıkış", null, (_, _) => quit());
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Show(); };
        _icon.BalloonTipClicked += (_, _) => Show();
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Notify(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(4000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    public void Show()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

public static class PowerService
{
    /// <summary>Bilgisayarı uyku moduna alır (kuyruk bitince isteğe bağlı).</summary>
    public static void Sleep() => SetSuspendState(false, false, false);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
