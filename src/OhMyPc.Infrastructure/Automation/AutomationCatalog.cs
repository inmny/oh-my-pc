using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.Automation;

public sealed class AutomationCatalog : IAutomationCatalog
{
    private readonly IReadOnlyDictionary<string, IAutomationValueOptionsProvider> _optionProviders;

    public AutomationCatalog(
        IEnumerable<IAutomationEventDescriptorProvider> descriptorProviders,
        IEnumerable<IAutomationValueOptionsProvider> optionProviders)
    {
        Events = descriptorProviders
            .SelectMany(provider => provider.Descriptors)
            .OrderBy(descriptor => descriptor.EventType)
            .ToArray();
        _optionProviders = optionProviders.ToDictionary(provider => provider.Key);
    }

    public IReadOnlyList<AutomationEventDescriptor> Events { get; }

    public Task<IReadOnlyList<AutomationValueOption>> GetOptionsAsync(
        string providerKey,
        CancellationToken cancellationToken = default) =>
        _optionProviders[providerKey].GetOptionsAsync(cancellationToken);
}
