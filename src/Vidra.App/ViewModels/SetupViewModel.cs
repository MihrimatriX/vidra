using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vidra.Core.Tools;

namespace Vidra.App.ViewModels;

/// <summary>İlk açılış: yt-dlp, ffmpeg ve Deno yoksa indirir (~150 MB, bir kez).</summary>
public sealed partial class SetupViewModel(AppHost host) : ObservableObject
{
    [ObservableProperty] private bool _isVisible = !host.Tools.AllInstalled;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _message = "Vidra'nın çalışması için üç bileşen gerekiyor. Bunlar bir kez indirilir ve kendi klasöründe güncel tutulur.";
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private bool _indeterminate;
    [ObservableProperty] private string? _error;

    [RelayCommand]
    private async Task Install()
    {
        Busy = true;
        Error = null;
        var progress = new Progress<SetupProgress>(p =>
        {
            Message = p.Message;
            Indeterminate = p.Fraction is null;
            if (p.Fraction is { } f) Fraction = f;
        });
        try
        {
            await host.Tools.EnsureInstalledAsync(progress);
            IsVisible = false;
        }
        catch (Exception e)
        {
            Error = $"İndirme başarısız: {e.Message}. İnternet bağlantını kontrol edip tekrar dene.";
        }
        finally
        {
            Busy = false;
        }
    }
}
