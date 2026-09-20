using System.Windows;
using OhMyPc.App.Services;
using OhMyPc.Infrastructure.Dsh;

namespace OhMyPc.App.Dialogs;

public partial class DshImportDialog : Window
{
    private sealed class EntryRow
    {
        public required SshConfigEntry Entry { get; init; }
        public string Label { get; init; } = "";
        public bool Selected { get; set; } = true;
    }

    public DshImportDialog(LocalizationService text, IReadOnlyList<SshConfigEntry> entries)
    {
        InitializeComponent();
        EntryList.ItemsSource = entries.Select(entry => new EntryRow
        {
            Entry = entry,
            Label = $"{entry.Alias}  ({entry.UserName}@{entry.HostName}:{entry.Port})"
        }).ToList();
        ImportButton.Content = text["Dsh_ImportButton"];
        if (EntryList.Items.Count == 0)
        {
            ImportButton.IsEnabled = false;
        }
    }

    /// <summary>勾选的条目；取消对话框时为 null。</summary>
    public IReadOnlyList<SshConfigEntry>? Selected { get; private set; }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        Selected = [.. EntryList.Items.OfType<EntryRow>()
            .Where(row => row.Selected)
            .Select(row => row.Entry)];
        DialogResult = true;
    }
}
