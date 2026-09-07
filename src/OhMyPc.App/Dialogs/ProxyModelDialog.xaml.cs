using System.Windows;
using OhMyPc.App.Services;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.App.Dialogs;

/// <summary>新增/编辑上游模型：名称、别名、上下文长度、思考档位与输入输出模态；费率统一来自 models.dev 目录，不再手工填写。</summary>
public partial class ProxyModelDialog : Window
{
    private readonly LocalizationService _text;

    public ProxyModelDialog(LocalizationService text, ProxyModelConfig? existing)
    {
        _text = text;
        InitializeComponent();
        NameBox.Text = existing?.Name ?? "";
        AliasBox.Text = existing?.Alias ?? "";
        ContextBox.Text = existing?.MaxContextLength?.ToString() ?? "";
        ProxyDialogChecks.Build(LevelsPanel, ProxyCatalog.ThinkingLevels, existing?.ThinkingLevels);
        ProxyDialogChecks.Build(InputPanel, ProxyCatalog.Modalities, existing?.InputModalities);
        ProxyDialogChecks.Build(OutputPanel, ProxyCatalog.Modalities, existing?.OutputModalities);
    }

    public ProxyModelConfig Model { get; private set; } = null!;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
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
        if (name.Length == 0)
        {
            ShowInvalid();
            return;
        }

        Model = new ProxyModelConfig
        {
            Name = name,
            Alias = string.IsNullOrWhiteSpace(AliasBox.Text) ? null : AliasBox.Text.Trim(),
            MaxContextLength = context,
            ThinkingLevels = ProxyDialogChecks.Collect(LevelsPanel, ProxyCatalog.ThinkingLevels),
            InputModalities = ProxyDialogChecks.Collect(InputPanel, ProxyCatalog.Modalities),
            OutputModalities = ProxyDialogChecks.Collect(OutputPanel, ProxyCatalog.Modalities)
        };
        DialogResult = true;
    }

    private void ShowInvalid() => System.Windows.MessageBox.Show(
        _text["Message_InvalidProxyModel"],
        _text["ProxyModelDialog_Title"],
        MessageBoxButton.OK,
        MessageBoxImage.Warning);
}
