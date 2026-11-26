using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Hrms.Persistence;

/// <summary>
/// Every DateTime column holds UTC. Writing a Local or Unspecified value is refused (it would silently shift by an offset),
/// and values read back are marked Kind=Utc.
/// </summary>
public sealed class UtcDateTimeConverter()
    : ValueConverter<DateTime, DateTime>(v => EnsureUtc(v), v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
{
    private static DateTime EnsureUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : throw new InvalidOperationException("DateTime values must be UTC (Kind=Utc) before they are stored.");
}
