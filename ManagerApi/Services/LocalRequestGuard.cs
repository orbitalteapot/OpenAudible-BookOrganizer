using Microsoft.Net.Http.Headers;

namespace ManagerApi.Services;

/// <summary>
/// Keeps web sites out of the backend.
///
/// CORS only stops a page from reading an answer; it does not stop a page on any web site from
/// sending a simple POST to the backend, which is enough to start or cancel a sort. So a request
/// that changes something must be JSON, which a browser will not send cross-site without a
/// preflight the CORS policy refuses. That holds for the container as much as for the desktop app.
///
/// The desktop backend also checks where a change comes from, and only answers to its own host
/// names (see <see cref="AllowedHosts"/>): without that a site can rebind its own name to 127.0.0.1
/// and read the settings and the library.
/// </summary>
public static class LocalRequestGuard
{
    /// <summary>The names the desktop backend answers to. Anything else is a rebound name.</summary>
    public static readonly string[] AllowedHosts = ["127.0.0.1", "localhost", "[::1]"];

    /// <summary>The Vite dev server, whose page talks to a backend started by hand.</summary>
    public static readonly string[] DevServerOrigins = ["http://localhost:5173", "http://127.0.0.1:5173"];

    /// <summary>
    /// What the desktop window's own page sends: it is loaded from disk, and depending on the Electron
    /// version that is sent as "null", as "file://" or not at all. A web site cannot send these with a
    /// JSON body, because the preflight fails.
    /// </summary>
    private static readonly string[] AppPageOrigins = ["null", "file://"];

    /// <summary>Why <paramref name="request"/> is refused, or null to let it through.</summary>
    /// <param name="checkOrigin">
    /// Whether a change must come from the app's own page. Only the desktop backend knows what that
    /// looks like: a container is often reached through a reverse proxy that changes the scheme or
    /// the host name the backend sees, so its own page's origin would not match and every change
    /// would be refused. The JSON rule alone already keeps other sites' requests out there.
    /// </param>
    public static (int Status, string Error)? Refusal(HttpRequest request, bool checkOrigin)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        {
            return null;
        }

        var origin = request.Headers.Origin.ToString();
        if (checkOrigin && origin.Length > 0 && !IsAllowedOrigin(origin, request))
        {
            return (StatusCodes.Status403Forbidden, "Requests from other web sites are not accepted.");
        }

        return MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) &&
               contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            ? null
            : (StatusCodes.Status415UnsupportedMediaType, "Send the request as JSON (Content-Type: application/json).");
    }

    private static bool IsAllowedOrigin(string origin, HttpRequest request)
    {
        return DevServerOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase) ||
               AppPageOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase) ||
               string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }
}
