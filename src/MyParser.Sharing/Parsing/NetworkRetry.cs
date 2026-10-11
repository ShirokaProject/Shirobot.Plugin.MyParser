using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Shirobot.Plugin.MyParser.Parsing;

/// <summary>Retries transient network operations before any chat delivery occurs.</summary>
public static class NetworkRetry
{
    public static async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation,
        int retryCount, CancellationToken cancellationToken = default)
    {
        var retries = Math.Clamp(retryCount, 0, 5);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await operation(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (attempt < retries && !cancellationToken.IsCancellationRequested && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(300 * (1 << attempt), 3000)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public static bool IsTransient(Exception exception)
    {
        // Authentication/certificate failures need configuration changes, not retries.
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is AuthenticationException or IProviderClassifiedException) return false;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or TaskCanceledException) return true;
            if (current is SocketException socket && socket.SocketErrorCode is
                SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.ConnectionRefused
                or SocketError.TimedOut or SocketError.NetworkDown or SocketError.NetworkUnreachable
                or SocketError.HostDown or SocketError.HostUnreachable or SocketError.HostNotFound
                or SocketError.TryAgain or SocketError.NotConnected or SocketError.Shutdown) return true;
            if (current is HttpRequestException http)
            {
                if (http.StatusCode is { } status)
                    return status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                        or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                        or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                if (http.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                    or HttpRequestError.ResponseEnded || http.InnerException is IOException) return true;
            }
        }
        return false;
    }
}
