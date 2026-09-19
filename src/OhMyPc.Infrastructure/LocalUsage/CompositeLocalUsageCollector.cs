using OhMyPc.Core;
using OhMyPc.Core.Domain;

namespace OhMyPc.Infrastructure.LocalUsage;

public sealed class CompositeLocalUsageCollector(
    TokscaleClient tokscale,
    DshUsageCollector dsh,
    ZcodeUsageCollector zcode,
    WorkbuddyUsageCollector workbuddy) : ILocalUsageCollector
{
    public async Task<IReadOnlyList<UsageObservation>> CollectAsync(
        bool fullHistory,
        CancellationToken cancellationToken = default)
    {
        var tokscaleTask = tokscale.CollectAsync(fullHistory, cancellationToken);
        var dshTask = dsh.CollectAsync(fullHistory, cancellationToken);
        var zcodeTask = zcode.CollectAsync(fullHistory, cancellationToken);
        var workbuddyTask = workbuddy.CollectAsync(fullHistory, cancellationToken);
        await Task.WhenAll(tokscaleTask, dshTask, zcodeTask, workbuddyTask);
        return [.. await tokscaleTask, .. await dshTask, .. await zcodeTask, .. await workbuddyTask];
    }
}
