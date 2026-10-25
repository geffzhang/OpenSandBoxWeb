namespace OpenSandbox.Web.Contracts;

public sealed record MetricsDto(double CpuCount, double CpuUsedPct, double MemTotalMib, double MemUsedMib, long Timestamp);
