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

    /// <summary>.NET'te NotifyIcon 127 karaktere izin verir (.NET Framework'te 63); sınır aşılırsa kısaltılır.</summary>
    public void SetTooltip(string text)
    {
        try { _icon.Text = text.Length > 127 ? text[..127] : text; }
        catch (ArgumentOutOfRangeException) { _icon.Text = text.Length > 63 ? text[..63] : text; }
    }

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
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;
    private static bool _awake;

    /// <summary>Bilgisayarı uyku moduna alır (kuyruk bitince isteğe bağlı).</summary>
    public static void Sleep() => SetSuspendState(false, false, false);

    /// <summary>
    /// Açıkken sistem kendiliğinden uykuya geçmez (ekran yine kapanabilir). İş parçacığına bağlı bir durumdur:
    /// hep aynı (arayüz) thread'inden çağrılmalı.
    /// </summary>
    public static void KeepAwake(bool on)
    {
        if (on == _awake) return;
        _awake = on;
        _ = SetThreadExecutionState(on ? EsContinuous | EsSystemRequired : EsContinuous);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
