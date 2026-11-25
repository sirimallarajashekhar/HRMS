namespace Hrms.Contracts;

public sealed record PingResponse(string Status, DateTimeOffset ServerTimeUtc);
