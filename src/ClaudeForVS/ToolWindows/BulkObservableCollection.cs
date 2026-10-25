using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ClaudeForVS.ToolWindows;

/// <summary>An observable collection that can swap in all of its items with one change notification, rather than one per item.</summary>
internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces every item with <paramref name="items"/>, raising a single Reset.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        this.CheckReentrancy();
        this.Items.Clear();
        foreach (T item in items)
        {
            this.Items.Add(item);
        }

        this.OnPropertyChanged(new PropertyChangedEventArgs(nameof(this.Count)));
        this.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        this.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
