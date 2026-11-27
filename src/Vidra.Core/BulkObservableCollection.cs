using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Vidra.Core;

/// <summary>
/// İçeriği tek seferde değiştirilebilen ObservableCollection. Clear + N kez Add, N+1 değişiklik olayı demektir:
/// binlerce videoluk bir listede her tuşa basışta arayüz her satır için ayrı ayrı yerleşim yapıyordu.
/// <see cref="ReplaceAll"/> tek bir Reset olayı yayar.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
