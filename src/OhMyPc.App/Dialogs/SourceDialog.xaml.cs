using System.Windows;
using OhMyPc.App.Services;
using OhMyPc.Core.Domain;
using OhMyPc.Infrastructure.Providers;

namespace OhMyPc.App.Dialogs;

public partial class SourceDialog : Window
{
    private readonly string _id;
    private readonly ProviderStatus _status;
    private readonly DateTimeOffset? _lastAttemptAt;
    private readonly DateTimeOffset? _lastSuccessAt;
    private readonly string? _lastError;
    private readonly int _consecutiveFailures;
    private readonly LocalizationService _text;

    public SourceDialog(LocalizationService text, DataSourceDefinition? source = null)
    {
        _text = text;
        InitializeComponent();
        KindBox.ItemsSource = Enum.GetValues<DataSourceKind>();
        source ??= new DataSourceDefinition { Name = text["SourceDialog_NewSource"], PollIntervalSeconds = 300 };
        _id = source.Id;
        _status = source.Status;
        _lastAttemptAt = source.LastAttemptAt;
        _lastSuccessAt = source.LastSuccessAt;
        _lastError = source.LastError;
        _consecutiveFailures = source.ConsecutiveFailures;
        NameBox.Text = source.Name;
        KindBox.SelectedItem = source.Kind;
        BaseUrlBox.Text = source.BaseUrl;
        ModelStatusUrlBox.Text = source.ModelStatusUrl;
        IntervalBox.Text = source.PollIntervalSeconds.ToString();
        EnabledBox.IsChecked = source.Enabled;
    }

    public DataSourceDefinition Source { get; private set; } = null!;
    public string? ApiKey => string.IsNullOrWhiteSpace(ApiKeyBox.Password) ? null : ApiKeyBox.Password.Trim();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var modelStatusUrl = ModelStatusUrlBox.Text.Trim();
        var hasValidModelStatusUrl = string.IsNullOrEmpty(modelStatusUrl)
            || Uri.TryCreate(modelStatusUrl, UriKind.Absolute, out var statusUrl)
                && statusUrl.Scheme is "http" or "https";
        if (string.IsNullOrWhiteSpace(NameBox.Text)
            || !Uri.TryCreate(BaseUrlBox.Text.Trim(), UriKind.Absolute, out var baseUrl)
            || baseUrl.Scheme is not ("http" or "https")
            || !hasValidModelStatusUrl
            || !int.TryParse(IntervalBox.Text, out var interval)
            || interval < 60)
        {
            System.Windows.MessageBox.Show(_text["Message_InvalidSource"], _text["SourceDialog_Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Source = new DataSourceDefinition
        {
            Id = _id,
            Name = NameBox.Text.Trim(),
            Kind = (DataSourceKind)KindBox.SelectedItem,
            BaseUrl = baseUrl.ToString().TrimEnd('/'),
            ModelStatusUrl = modelStatusUrl,
            Enabled = EnabledBox.IsChecked == true,
            PollIntervalSeconds = interval,
            Status = _status,
            LastAttemptAt = _lastAttemptAt,
            LastSuccessAt = _lastSuccessAt,
            LastError = _lastError,
            ConsecutiveFailures = _consecutiveFailures
        };
        DialogResult = true;
    }

    private void KindBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (KindBox.SelectedItem is DataSourceKind.ZhipuCodingPlan && string.IsNullOrWhiteSpace(BaseUrlBox.Text))
        {
            BaseUrlBox.Text = ZhipuCodingPlanProvider.DefaultBaseUrl;
        }
    }
}
