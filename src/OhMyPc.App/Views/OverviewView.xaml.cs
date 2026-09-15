using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using OhMyPc.App.Dialogs;
using OhMyPc.App.Services;
using OhMyPc.App.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace OhMyPc.App.Views;

public partial class OverviewView : UserControl
{
    private bool _chartsWired;

    public OverviewView() => InitializeComponent();

    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private LocalizationService Text => ((App)System.Windows.Application.Current).Services.GetRequiredService<LocalizationService>();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_chartsWired || DataContext is not MainViewModel viewModel) return;
        _chartsWired = true;
        // 自研悬浮框直接挂到图表控件上（不依赖 LiveCharts 的 tooltip 管线）。
        // 关闭默认 tooltip 必须用 Hidden 而非 Tooltip=null：Loaded 应用主题时会 Tooltip ??= 默认值，null 会被回填
        viewModel.CandleTooltip.Attach(WeeklyUsageChart);
        viewModel.MessageTooltip.Attach(WeeklyMessageChart);
        WeeklyUsageChart.TooltipPosition = LiveChartsCore.Measure.TooltipPosition.Hidden;
        WeeklyMessageChart.TooltipPosition = LiveChartsCore.Measure.TooltipPosition.Hidden;
        WeeklyUsageChart.Tooltip = null;
        WeeklyMessageChart.Tooltip = null;
    }

    private async void ImportEnvironment_Click(object sender, RoutedEventArgs e)
    {
        var count = await ViewModel.ImportEnvironmentAsync();
        System.Windows.MessageBox.Show(Text.Format("Message_ImportedSources", count), "Oh My PC", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SourceDialog(Text) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) await ViewModel.SaveSourceAsync(dialog.Source, dialog.ApiKey);
    }

    private async void EditQuotaSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuotaSourceCardViewModel card) return;
        var dialog = new SourceDialog(Text, card.Source) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) await ViewModel.SaveSourceAsync(dialog.Source, dialog.ApiKey);
    }

    private async void RefreshModelStatus_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QuotaSourceCardViewModel card)
        {
            await ViewModel.RefreshModelStatusAsync(card.SourceId);
        }
    }

    private async void DeleteQuotaSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QuotaSourceCardViewModel card) return;
        var answer = System.Windows.MessageBox.Show(
            Text.Format("Message_DeleteSource", card.SourceName),
            "Oh My PC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
        {
            ViewModel.SelectedSource = card.Source;
            await ViewModel.DeleteSelectedSourceAsync();
        }
    }
}
