using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Vidra.App.Services;
using Vidra.App.ViewModels;
using Vidra.Core.Models;
using Vidra.Core.Queue;

namespace Vidra.App;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private bool _exiting;
    private bool _toldAboutTray;
    private IInputElement? _focusBeforePalette;
    private ThumbButtonInfo? _thumbPause;
    private ThumbButtonInfo? _thumbResume;

    /// <summary>Kütüphaneden dışarı dosya sürüklenirken kendi penceremiz bırakma hedefi olmasın.</summary>
    internal static bool InternalDrag { get; set; }

    public MainWindow(MainViewModel vm, AppHost host)
    {
        _host = host;
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        RestorePlacement();
        SetupTaskbar();
        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Progress)) UpdateTaskbar(vm.Progress); };
        vm.Palette.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen)) OnPaletteToggled(vm.Palette.IsOpen); };
    }

    /// <summary>Kapat düğmesi: ayar açıksa tepsiye küçült (indirmeler sürer), değilse çık.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        SavePlacement();
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
        SavePlacement();
        Hide();
        await ((App)Application.Current).ShutdownGracefullyAsync();
    }

    // ================= Pencere konumu =================

    private void RestorePlacement()
    {
        if (_host.Settings.Window is not { } b) return;
        var r = new Rect(b.Left, b.Top, Math.Max(b.Width, MinWidth), Math.Max(b.Height, MinHeight));
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // Başlık şeridi görünür bir yerde değilse (ör. ikinci ekran çıkarıldı) varsayılan: ortada
        if (!screen.IntersectsWith(new Rect(r.Left + 60, r.Top, Math.Max(1, r.Width - 120), 30))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = r.Left;
        Top = r.Top;
        Width = r.Width;
        Height = r.Height;
        if (b.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.IsEmpty || double.IsNaN(r.Width) || r.Width < 100) return;
        var max = WindowState == WindowState.Maximized;
        _host.SettingsStore.Update(s => s.Window = new WindowBounds(r.Left, r.Top, r.Width, r.Height, max));
    }

    // ================= Görev çubuğu: toplam ilerleme + küçük resim düğmeleri =================

    private void SetupTaskbar()
    {
        _thumbPause = new ThumbButtonInfo { Description = "Tümünü duraklat", ImageSource = ThumbIcon("I.Pause"), Visibility = Visibility.Collapsed };
        _thumbResume = new ThumbButtonInfo { Description = "Tümünü sürdür", ImageSource = ThumbIcon("I.Play"), Visibility = Visibility.Collapsed };
        _thumbPause.Click += (_, _) => _host.Queue.PauseAll();
        _thumbResume.Click += (_, _) => _host.Queue.ResumeAll();
        TaskbarItemInfo = new TaskbarItemInfo { ThumbButtonInfos = new ThumbButtonInfoCollection { _thumbPause, _thumbResume } };
    }

    /// <summary>Çizgi ikonu görev çubuğu temasına uygun renkte, 24×24 tuvali koruyarak çizer.</summary>
    private ImageSource ThumbIcon(string key)
    {
        var ink = ThemeService.TaskbarIsLight() ? new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)) : Brushes.White;
        var pen = new Pen(ink, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        group.Children.Add(new GeometryDrawing(null, pen, (Geometry)FindResource(key)));
        var img = new DrawingImage(group);
        img.Freeze();
        return img;
    }

    private void UpdateTaskbar(QueueProgress p)
    {
        var info = TaskbarItemInfo;
        if (info is null) return;
        var waiting = p.Paused + p.Queued;
        info.ProgressState =
            p.Running > 0 ? TaskbarItemProgressState.Normal
            : waiting > 0 ? TaskbarItemProgressState.Paused
            : p.Errors > 0 ? TaskbarItemProgressState.Error
            : TaskbarItemProgressState.None;
        // Yalnızca hata kaldıysa çubuk tam ve kırmızı: "bakman gereken bir şey var"
        info.ProgressValue = info.ProgressState == TaskbarItemProgressState.Error ? 1 : Math.Max(0.02, p.Fraction);
        info.Description = p.IsEmpty ? "" : p.Describe();
        _thumbPause!.Visibility = p.Running + p.Queued > 0 ? Visibility.Visible : Visibility.Collapsed;
        _thumbResume!.Visibility = p.Paused > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= Klavye =================

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || _vm.Setup.IsVisible) return;
        if (e.Key == Key.Escape && _vm.Palette.IsOpen)
        {
            _vm.Palette.Close();
            e.Handled = true;
            return;
        }
        // Palet açıkken kısayollar onun metin kutusunu bozmasın (Ctrl+1…9 ise sayfa değiştirip paleti kapatır)
        if (Keyboard.Modifiers != ModifierKeys.Control || _vm.Palette.IsOpen) return;
        switch (e.Key)
        {
            // Bağlantı kutusu (tarayıcıdaki adres çubuğu gibi): metni seçili odakla
            case Key.L:
                e.Handled = true;
                _vm.Go(ViewModels.Page.Download);
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FocusInPage("UrlBox"));
                break;
            // Bu sayfadaki arama kutusu; sayfada yoksa komut paleti
            case Key.F:
                e.Handled = true;
                if (!FocusInPage("SearchBox", "QueryBox", "UrlBox")) _vm.Palette.Open();
                break;
            // Metin kutusu dışında yapıştır: bağlantıyı hangi ekrandan olursa olsun aç
            case Key.V when !IsTextInput(Keyboard.FocusedElement):
                e.Handled = true;
                _ = _vm.PasteAsync();
                break;
        }
    }

    private static bool IsTextInput(IInputElement? el) => el is TextBoxBase or PasswordBox or ComboBox { IsEditable: true };

    /// <summary>Görünen sayfada adı verilen ilk görünür metin kutusunu odaklar ve metnini seçer.</summary>
    private bool FocusInPage(params string[] names)
    {
        foreach (var name in names)
        {
            if (FindVisible(PageHost, name) is not { } el) continue;
            el.Focus();
            Keyboard.Focus(el);
            if (el is TextBox tb) tb.SelectAll();
            return true;
        }
        return false;
    }

    private static FrameworkElement? FindVisible(DependencyObject root, string name)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            if (d is FrameworkElement { IsVisible: true } fe && fe.Name == name) return fe;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
        }
        return null;
    }

    // ================= Komut paleti =================

    private void OnPaletteToggled(bool open)
    {
        if (open)
        {
            _focusBeforePalette = Keyboard.FocusedElement;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                PaletteBox.Focus();
                Keyboard.Focus(PaletteBox);
            });
        }
        else if (_focusBeforePalette is UIElement { IsVisible: true } prev)
        {
            prev.Focus();
        }
    }

    private void PaletteBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _vm.Palette.Move(1);
                break;
            case Key.Up:
                _vm.Palette.Move(-1);
                break;
            case Key.Enter:
                _vm.Palette.RunCommand.Execute(null);
                break;
            case Key.Escape:
                _vm.Palette.Close();
                break;
            default:
                return;
        }
        e.Handled = true;
        if (_vm.Palette.Selected is { } s) PaletteList.ScrollIntoView(s);
    }

    private void PaletteBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == PaletteLayer) _vm.Palette.Close();
    }

    // ================= Bildirim =================

    private void Toast_MouseEnter(object sender, MouseEventArgs e) => _vm.HoldToast(true);
    private void Toast_MouseLeave(object sender, MouseEventArgs e) => _vm.HoldToast(false);

    // ================= Sürükle-bırak =================

    private void Window_PreviewDragEnter(object sender, DragEventArgs e)
    {
        if (InternalDrag || _vm.Setup.IsVisible || !LinkDrop.CanAccept(e.Data)) return;
        DropLayer.Visibility = Visibility.Visible;
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!InternalDrag) return;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void DropLayer_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void DropLayer_DragLeave(object sender, DragEventArgs e) => DropLayer.Visibility = Visibility.Collapsed;

    private async void DropLayer_Drop(object sender, DragEventArgs e)
    {
        DropLayer.Visibility = Visibility.Collapsed;
        e.Handled = true;
        var text = LinkDrop.ReadText(e.Data);
        Activate();
        if (text is null || !await _vm.OpenTextAsync(text, "Sürükle-bırak")) _vm.ShowToast("Bırakılanda YouTube bağlantısı bulunamadı");
    }
}
