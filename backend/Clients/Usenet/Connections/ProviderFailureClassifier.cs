using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Clients.Usenet.Connections;

public static partial class ProviderFailureClassifier
{
    [GeneratedRegex(@"\b(\d{3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex NntpResponseCodeRegex();

    public static ProviderFailureInfo Classify(Exception exception, string? operation = null)
    {
        var detail = CollectDetail(exception);
        var upper = detail.ToUpperInvariant();
        var responseCode = ExtractNntpResponseCode(detail);

        if (exception.TryGetCausingException<CouldNotLoginToUsenetException>(out _))
            return Build(ProviderFailureCategory.Authentication, "Invalid credentials or auth rejected by server", detail, responseCode, operation);

        if (exception.TryGetCausingException<UsenetArticleNotFoundException>(out _))
            return Build(ProviderFailureCategory.MissingArticle, "Article not found on provider", detail, responseCode, operation);

        if (exception.TryGetCausingException<CouldNotConnectToUsenetException>(out _))
            return ClassifyConnectionFailure(detail, upper, responseCode, operation);

        if (exception.IsTimeoutException())
            return Build(ProviderFailureCategory.Timeout, "Timed out waiting for NNTP response", detail, responseCode, operation);

        if (LooksLikeTooManyConnections(upper, responseCode))
            return Build(ProviderFailureCategory.TooManyConnections, "Too many concurrent connections for this account", detail, responseCode, operation);

        if (LooksLikeAuthenticationFailure(upper, responseCode))
            return Build(ProviderFailureCategory.Authentication, "Authentication rejected by server", detail, responseCode, operation);

        if (exception.TryGetCausingException<AuthenticationException>(out _))
            return Build(ProviderFailureCategory.Ssl, "TLS/SSL handshake failed", detail, responseCode, operation);

        if (exception.TryGetCausingException<SocketException>(out var socketException) && socketException is not null)
            return ClassifySocketException(socketException, detail, responseCode, operation);

        if (LooksLikeServerError(upper, responseCode))
            return Build(ProviderFailureCategory.ServerError, "Server returned a temporary or protocol error", detail, responseCode, operation);

        return Build(
            ProviderFailureCategory.Unknown,
            $"Unhandled {exception.GetType().Name}",
            detail,
            responseCode,
            operation);
    }

    private static ProviderFailureInfo ClassifyConnectionFailure
    (
        string detail,
        string upper,
        int? responseCode,
        string? operation
    )
    {
        if (LooksLikeTooManyConnections(upper, responseCode))
            return Build(ProviderFailureCategory.TooManyConnections, "Too many concurrent connections for this account", detail, responseCode, operation);

        if (LooksLikeAuthenticationFailure(upper, responseCode))
            return Build(ProviderFailureCategory.Authentication, "Authentication rejected while opening connection", detail, responseCode, operation);

        return Build(ProviderFailureCategory.Connection, "Could not connect to NNTP host", detail, responseCode, operation);
    }

    private static ProviderFailureInfo ClassifySocketException
    (
        SocketException socketException,
        string detail,
        int? responseCode,
        string? operation
    )
    {
        var summary = socketException.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "Connection refused by NNTP host",
            SocketError.TimedOut => "Socket timed out connecting to NNTP host",
            SocketError.HostNotFound or SocketError.HostUnreachable => "NNTP host could not be reached",
            _ => $"Socket error ({socketException.SocketErrorCode})"
        };

        return Build(ProviderFailureCategory.Connection, summary, detail, responseCode, operation);
    }

    private static bool LooksLikeTooManyConnections(string upper, int? responseCode)
    {
        if (responseCode is 502 or 503 &&
            (upper.Contains("CONNECTION", StringComparison.Ordinal) ||
             upper.Contains("MAXIMUM", StringComparison.Ordinal) ||
             upper.Contains("LIMIT", StringComparison.Ordinal)))
            return true;

        return upper.Contains("TOO MANY", StringComparison.Ordinal) ||
               upper.Contains("MAX CONNECTION", StringComparison.Ordinal) ||
               upper.Contains("MAXIMUM CONNECTION", StringComparison.Ordinal) ||
               upper.Contains("CONNECTION LIMIT", StringComparison.Ordinal) ||
               upper.Contains("EXCEEDED", StringComparison.Ordinal) && upper.Contains("CONNECTION", StringComparison.Ordinal);
    }

    private static bool LooksLikeAuthenticationFailure(string upper, int? responseCode)
    {
        if (responseCode is 480 or 481 or 482 or 483) return true;

        return upper.Contains("AUTHENTICATION", StringComparison.Ordinal) ||
               upper.Contains("AUTHORIZATION FAILED", StringComparison.Ordinal) ||
               upper.Contains("INVALID PASSWORD", StringComparison.Ordinal) ||
               upper.Contains("LOGIN FAILED", StringComparison.Ordinal) ||
               upper.Contains("AUTH FAIL", StringComparison.Ordinal) ||
               upper.Contains("BAD USER", StringComparison.Ordinal);
    }

    private static bool LooksLikeServerError(string upper, int? responseCode)
    {
        if (responseCode is 400 or 500 or 502 or 503 or 504) return true;

        return upper.Contains("SERVICE UNAVAILABLE", StringComparison.Ordinal) ||
               upper.Contains("TEMPORAR", StringComparison.Ordinal) ||
               upper.Contains("SERVER ERROR", StringComparison.Ordinal);
    }

    private static string CollectDetail(Exception exception)
    {
        var parts = new List<string>();
        var current = exception;
        while (current is not null)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
                parts.Add(current.Message.Trim());

            current = current.InnerException;
        }

        return parts.Count == 0 ? exception.GetType().Name : string.Join(" | ", parts);
    }

    private static int? ExtractNntpResponseCode(string detail)
    {
        foreach (Match match in NntpResponseCodeRegex().Matches(detail))
        {
            if (!int.TryParse(match.Groups[1].Value, out var code)) continue;
            if (code is >= 100 and <= 599) return code;
        }

        return null;
    }

    private static ProviderFailureInfo Build
    (
        ProviderFailureCategory category,
        string summary,
        string detail,
        int? responseCode,
        string? operation
    )
    {
        return new ProviderFailureInfo(category, summary, detail, responseCode, operation);
    }
}
