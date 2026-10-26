namespace OpenSandbox.Web.Tests.Volumes;

public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
