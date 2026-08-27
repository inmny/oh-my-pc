using System.Windows;
using OhMyPc.App.Services;
using OhMyPc.App.ViewModels;

namespace OhMyPc.App.Dialogs;

/// <summary>从远端拉取的模型列表中勾选导入；已存在模型勾选后用元数据更新。</summary>
public partial class ProxyModelImportDialog : Window
{
    private readonly IReadOnlyList<ProxyImportModelRow> _rows;

    public ProxyModelImportDialog(LocalizationService text, string providerTitle, IReadOnlyList<ProxyImportModelRow> rows)
    {
        _rows = rows;
        InitializeComponent();
        ProviderTitleText.Text = providerTitle;
        RowsGrid.ItemsSource = rows;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Checked = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Checked = false;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
