namespace OhMyPc.Infrastructure.InputStatus;

public sealed class InputSourceStatusSnapshot
{
    public string SourceId { get; init; } = "";
    public string StatusUrl { get; init; } = "";
    public IReadOnlyList<InputModelStatusSnapshot> Models { get; init; } = [];
    public DateTimeOffset LastAttemptAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public string? Error { get; init; }
}

public sealed class InputModelStatusSnapshot
{
    public string Model { get; init; } = "";
    public bool Available { get; init; }
    public long? LatencyMilliseconds { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<bool> Samples { get; init; } = [];
}
