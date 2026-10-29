using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Vidra.App.ViewModels;
using Vidra.Core.Models;

namespace Vidra.App.Views;

public partial class LibraryView : UserControl
{
    private Point _dragStart;
    private HistoryEntry? _dragItem;

    public LibraryView()
    {
        InitializeComponent();
        PreviewMouseLeftButtonUp += (_, _) => _dragItem = null; // tıklama bitti: sürükleme adayı yok
    }

    private void Cards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm) vm.Cards.Resize(e.NewSize.Width - SystemParameters.VerticalScrollBarWidth);
    }

    /// <summary>Sürüklemeye aday: düğmelerden başlayan basışlar sayılmaz (tıklamaları bozmasın).</summary>
    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != sender; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase) return;
        _dragItem = (sender as FrameworkElement)?.DataContext as HistoryEntry;
        _dragStart = e.GetPosition(this);
    }

    /// <summary>Kartı dışarı sürükle: dosya Gezgin'e, bir sohbet uygulamasına ya da düzenleyiciye kopyalanır.</summary>
    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        // Basış başka kartta başladıysa ya da tuş bırakıldıysa eski aday sürüklenmesin
        if (e.LeftButton != MouseButtonState.Pressed || !ReferenceEquals((sender as FrameworkElement)?.DataContext, _dragItem))
        {
            if (e.LeftButton != MouseButtonState.Pressed) _dragItem = null;
            return;
        }
        if (_dragItem is not { FilePath: { } path }) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragItem = null;
        if (!File.Exists(path)) return;
        MainWindow.InternalDrag = true;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy); }
        finally { MainWindow.InternalDrag = false; }
    }
}
