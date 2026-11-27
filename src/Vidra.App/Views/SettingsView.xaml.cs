using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vidra.App.ViewModels;
using Vidra.Core;

namespace Vidra.App.Views;

public partial class SettingsView : UserControl
{
    /// <summary>Gelişmiş bölümler: varsayılan gizli, "Gelişmiş ayarları göster" ya da aramayla görünür.</summary>
    private static readonly HashSet<string> Advanced = ["SecNet", "SecTools"];
    private static readonly string[] Sections = ["SecAccount", "SecFiles", "SecFormat", "SecLook", "SecNet", "SecTools"];

    private readonly Dictionary<string, string> _index = [];

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var vm = DataContext as SettingsViewModel;
            AdvancedBox.IsChecked = vm?.ShowAdvanced == true;
            // Başka ekrandan "Ayarlar › Bileşenler" gibi bir bölüme gelindiyse: gerekirse gelişmişi aç, oraya kaydır
            if (vm?.PendingSection is { } section)
            {
                vm.PendingSection = null;
                if (Advanced.Contains(section)) AdvancedBox.IsChecked = vm.ShowAdvanced = true;
                ApplyFilter();
                Dispatcher.BeginInvoke(() => JumpTo(section), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else ApplyFilter();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { FilterBox.Focus(); FilterBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Escape && FilterBox.IsKeyboardFocused && FilterBox.Text.Length > 0) { FilterBox.Clear(); e.Handled = true; }
        };
    }

    /// <summary>Sol menüdeki bölüme kaydır.</summary>
    private void Jump_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string name }) JumpTo(name);
    }

    private void JumpTo(string name)
    {
        if (FindName(name) is not FrameworkElement { IsVisible: true } target) return;
        var pos = target.TransformToAncestor((Visual)Scroller.Content).Transform(new Point(0, 0));
        Scroller.ScrollToVerticalOffset(pos.Y);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.ShowAdvanced = AdvancedBox.IsChecked == true;
        ApplyFilter();
    }

    /// <summary>
    /// Arama boşsa: temel bölümler + (isteğe bağlı) gelişmişler. Arama varsa: yazılarında geçen her bölüm, gelişmiş olsa da.
    /// Eşleşme büyük/küçük harf ve aksan duyarsız (ör. "gorunum" → "Davranış &amp; görünüm").
    /// </summary>
    private void ApplyFilter()
    {
        var q = FilterBox.Text.Trim();
        var showAdvanced = AdvancedBox.IsChecked == true;
        var any = false;
        foreach (var name in Sections)
        {
            if (FindName(name) is not FrameworkElement section) continue;
            bool visible = q.Length > 0 ? Matches(IndexOf(name, section), q) : showAdvanced || !Advanced.Contains(name);
            section.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            any |= visible;
        }
        // Sol menüde de yalnızca görünen bölümler
        foreach (var link in ((Panel)FilterBox.Parent).Children.OfType<Button>())
            if (link.CommandParameter is string target && FindName(target) is FrameworkElement s)
                link.Visibility = s.Visibility;
        NoMatch.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool Matches(string text, string query) =>
        Fmt.Tr.CompareInfo.IndexOf(text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private string IndexOf(string name, DependencyObject section)
    {
        if (_index.TryGetValue(name, out var text)) return text;
        var sb = new StringBuilder();
        Collect(section, sb);
        return _index[name] = sb.ToString();
    }

    /// <summary>Bölümdeki tüm görünen yazılar: başlıklar, açıklamalar, anahtar adları ve seçenek listelerinin etiketleri.</summary>
    private static void Collect(object node, StringBuilder sb)
    {
        switch (node)
        {
            case TextBlock t: sb.Append(t.Text).Append(' '); break;
            case HeaderedContentControl { Header: string h }: sb.Append(h).Append(' '); break;
            case ContentControl { Content: string c }: sb.Append(c).Append(' '); break;
        }
        if (node is FrameworkElement { Tag: string tag }) sb.Append(tag).Append(' ');
        if (node is ItemsControl { ItemsSource: IEnumerable items })
            foreach (var item in items)
                foreach (var prop in new[] { "Label", "Name", "Hint", "Purpose" })
                    if (item?.GetType().GetProperty(prop)?.GetValue(item) is string v) sb.Append(v).Append(' ');
        if (node is DependencyObject d)
            foreach (var child in LogicalTreeHelper.GetChildren(d)) Collect(child, sb);
    }
}
