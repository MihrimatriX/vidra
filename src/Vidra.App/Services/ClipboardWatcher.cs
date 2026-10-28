using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vidra.Core.YtDlp;

namespace Vidra.App.Services;

/// <summary>
/// Pano değişikliklerini yoklama yapmadan dinler (AddClipboardFormatListener / WM_CLIPBOARDUPDATE).
/// YouTube bağlantısı kopyalanınca olay tetiklenir; aynı bağlantı ardışık iki kez bildirilmez.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private HwndSource? _source;
    private string? _last;

    public event EventHandler<string>? LinkCopied;
    public bool Enabled { get; set; } = true;

    /// <summary>Uygulamanın kendi kopyaladığı metni yoksaymak için (ör. "komutu kopyala").</summary>
    public void Ignore(string text) => _last = text.Trim();

    public void Attach(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(Hook);
        AddClipboardFormatListener(hwnd);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WmClipboardUpdate && Enabled) OnChanged();
        return IntPtr.Zero;
    }

    private void OnChanged()
    {
        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText().Trim();
        }
        catch (COMException) { return; } // başka bir uygulama panoyu kilitli tutuyor olabilir
        if (text.Length is 0 or > 2000 || text == _last) return;
        _last = text;
        if (YouTubeUrl.Classify(text) is not null and not Vidra.Core.Models.LinkKind.Feed) LinkCopied?.Invoke(this, text);
    }

    public void Dispose()
    {
        if (_source is null) return;
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(Hook);
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
