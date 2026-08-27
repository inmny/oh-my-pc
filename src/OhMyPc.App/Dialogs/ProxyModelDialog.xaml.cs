using System.Globalization;
using System.Windows;
using OhMyPc.App.Services;
using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.App.Dialogs;

/// <summary>新增/编辑上游模型：名称、别名、上下文长度、思考档位、输入输出模态与费用。</summary>
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
        CostInputBox.Text = existing?.Cost?.Input?.ToString(CultureInfo.InvariantCulture) ?? "";
        CostOutputBox.Text = existing?.Cost?.Output?.ToString(CultureInfo.InvariantCulture) ?? "";
        CostCacheReadBox.Text = existing?.Cost?.CacheRead?.ToString(CultureInfo.InvariantCulture) ?? "";
        CostCacheWriteBox.Text = existing?.Cost?.CacheWrite?.ToString(CultureInfo.InvariantCulture) ?? "";
        ProxyDialogChecks.Build(LevelsPanel, ProxyCatalog.ThinkingLevels, existing?.ThinkingLevels);
        ProxyDialogChecks.Build(InputPanel, ProxyCatalog.Modalities, existing?.InputModalities);
        ProxyDialogChecks.Build(OutputPanel, ProxyCatalog.Modalities, existing?.OutputModalities);
    }

    public ProxyModelConfig Model { get; private set; } = null!;

    private bool TryReadCost(out ProxyModelCost? cost)
    {
        cost = null;
        if (!TryParseDecimal(CostInputBox, out var input)
            || !TryParseDecimal(CostOutputBox, out var output)
            || !TryParseDecimal(CostCacheReadBox, out var cacheRead)
            || !TryParseDecimal(CostCacheWriteBox, out var cacheWrite))
        {
            return false;
        }
        if (input is null && output is null && cacheRead is null && cacheWrite is null) return true;
        cost = new ProxyModelCost
        {
            Input = input,
            Output = output,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite
        };
        return true;
    }

    private static bool TryParseDecimal(System.Windows.Controls.TextBox box, out decimal? value)
    {
        value = null;
        var text = box.Text.Trim();
        if (text.Length == 0) return true;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed < 0) return false;
        value = parsed;
        return true;
    }

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
        if (name.Length == 0 || !TryReadCost(out var cost))
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
            OutputModalities = ProxyDialogChecks.Collect(OutputPanel, ProxyCatalog.Modalities),
            Cost = cost
        };
        DialogResult = true;
    }

    private void ShowInvalid() => System.Windows.MessageBox.Show(
        _text["Message_InvalidProxyModel"],
        _text["ProxyModelDialog_Title"],
        MessageBoxButton.OK,
        MessageBoxImage.Warning);
}
