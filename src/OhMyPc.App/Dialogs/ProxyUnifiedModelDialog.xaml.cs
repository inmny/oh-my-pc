using System.Windows;
using OhMyPc.App.Services;
using OhMyPc.App.ViewModels;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.App.Dialogs;

/// <summary>统一配置某个上游模型名在所有 Provider 中的上下文/思考档位/模态。</summary>
public partial class ProxyUnifiedModelDialog : Window
{
    private readonly LocalizationService _text;
    private readonly ProxyUnifiedModelRowViewModel _row;

    public ProxyUnifiedModelDialog(LocalizationService text, ProxyUnifiedModelRowViewModel row)
    {
        _text = text;
        _row = row;
        InitializeComponent();
        ModelNameText.Text = row.Name;
        ProviderCountText.Text = text.Format("ProxyUnifiedDialog_Providers", row.ProviderCount);
        ContextBox.Text = row.ContextWindow?.ToString() ?? "";
        ProxyDialogChecks.Build(LevelsPanel, ProxyCatalog.ThinkingLevels, row.ThinkingLevels);
        ProxyDialogChecks.Build(InputPanel, ProxyCatalog.Modalities, row.InputModalities);
        ProxyDialogChecks.Build(OutputPanel, ProxyCatalog.Modalities, row.OutputModalities);
    }

    public UnifiedModelEdit Result { get; private set; } = null!;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        long? context = null;
        if (ContextBox.Text.Trim().Length > 0)
        {
            if (!long.TryParse(ContextBox.Text.Trim(), out var parsed) || parsed <= 0)
            {
                ShowInvalid();
                return;
            }
            context = parsed;
        }
        Result = new UnifiedModelEdit(
            context,
            ProxyDialogChecks.Collect(LevelsPanel, ProxyCatalog.ThinkingLevels),
            ProxyDialogChecks.Collect(InputPanel, ProxyCatalog.Modalities),
            ProxyDialogChecks.Collect(OutputPanel, ProxyCatalog.Modalities));
        DialogResult = true;
    }

    private void ShowInvalid() => System.Windows.MessageBox.Show(
        _text["Message_InvalidProxyModel"],
        _text["ProxyUnifiedDialog_Title"],
        MessageBoxButton.OK,
        MessageBoxImage.Warning);
}
