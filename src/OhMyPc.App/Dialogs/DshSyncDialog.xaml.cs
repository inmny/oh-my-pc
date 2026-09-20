using System.Windows;
using OhMyPc.App.Services;
using OhMyPc.Infrastructure.Dsh;

namespace OhMyPc.App.Dialogs;

public partial class DshSyncDialog : Window
{
    private sealed class EntryRow
    {
        public required DshConfigItem Item { get; init; }
        public string Label => Item.DisplayName;
        public string Description => Item.Description;
        public bool Selected { get; set; }
    }

    private readonly LocalizationService _text;

    public DshSyncDialog(LocalizationService text, IReadOnlyList<DshConfigItem> items, IEnumerable<string> preselected)
    {
        _text = text;
        InitializeComponent();
        var selected = preselected.ToHashSet(StringComparer.Ordinal);
        EntryList.ItemsSource = items.Select(item => new EntryRow
        {
            Item = item,
            Selected = selected.Contains(item.Id)
        }).ToList();
        SyncButton.Content = text["Dsh_SyncButton"];
    }

    /// <summary>勾选的条目 id；取消对话框时为 null。</summary>
    public IReadOnlyList<string>? Selected { get; private set; }

    private void Sync_Click(object sender, RoutedEventArgs e)
    {
        var ids = EntryList.Items.OfType<EntryRow>()
            .Where(row => row.Selected)
            .Select(row => row.Item.Id)
            .ToList();
        if (ids.Count == 0)
        {
            System.Windows.MessageBox.Show(
                _text["Message_DshSyncNone"],
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Selected = ids;
        DialogResult = true;
    }
}
