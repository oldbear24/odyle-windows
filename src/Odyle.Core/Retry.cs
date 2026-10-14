using System.Net;
using System.Net.Http.Headers;

namespace Odyle.Core;
// The server asked the app to slow down (429). Requests pause until Until; the publisher
// session is kept, so the live reading is not invalidated.
public sealed class ServerBusyException(string message, DateTimeOffset until) : HttpRequestException(message, null, HttpStatusCode.TooManyRequests)
{
    public DateTimeOffset Until { get; } = until;
}
// When to try again after the server could not be reached or asked the app to wait.
public static class Retry
{
    private static readonly TimeSpan[] Steps = [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)];
    public static readonly TimeSpan Steady = TimeSpan.FromMinutes(5), Busy = TimeSpan.FromSeconds(60);
    // 15 s, 30 s, 1 min and 2 min after the first, second, third and fourth failure, then every 5 minutes.
    public static TimeSpan Backoff(int attempt) => attempt < Steps.Length ? Steps[Math.Max(0, attempt)] : Steady;
    // Retry-After as seconds or an HTTP date. Missing or unusable: a minute. At least a
    // second (a date already past means now) and at most an hour.
    public static TimeSpan After(string? header, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(header) || !RetryConditionHeaderValue.TryParse(header.Trim(), out var value)) return Busy;
        var wait = value.Delta ?? (value.Date is { } date ? date - now : Busy);
        return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;
    }
}
