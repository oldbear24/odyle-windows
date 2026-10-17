using System.Text.RegularExpressions;

namespace Odyle.Core;

public sealed record ConnectionDetails(string Endpoint, string Code)
{
    public static string NormalizeEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException(Localizer.T("Use an HTTPS server address, or HTTP on localhost. Do not include credentials or query parameters."));
        return uri.AbsoluteUri.TrimEnd('/');
    }

    public static ConnectionDetails Parse(string input, string fallbackEndpoint)
    {
        input = input.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var link))
        {
            const string marker = "#odyle-pair=";
            if (!link.Fragment.StartsWith(marker, StringComparison.Ordinal))
                throw new ArgumentException(Localizer.T("Copy the connection link from Pair Windows app on the dashboard."));
            var code = NormalizeCode(Uri.UnescapeDataString(link.Fragment[marker.Length..]));
            var address = new UriBuilder(link) { Fragment = "" }.Uri.AbsoluteUri;
            return new(NormalizeEndpoint(address), code);
        }
        return new(NormalizeEndpoint(fallbackEndpoint), NormalizeCode(input));
    }

    // "Open in Odyle" on the dashboard's pairing dialog: odyle://pair?link=<the connection link>. Only
    // that form is taken, and the link inside is checked like a pasted one (HTTPS, or HTTP on localhost).
    public const string Scheme = "odyle";
    public static ConnectionDetails FromAppLink(string input)
    {
        input = input.Trim();
        if (input.Length > 2048 || !Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != Scheme || !string.Equals(uri.Host, "pair", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath is not ("" or "/"))
            throw new ArgumentException(Localizer.T("This is not an Odyle pairing link. Copy the connection link from Pair Windows app on the dashboard."));
        var links = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2)).Where(part => part.Length == 2 && part[0] == "link").Select(part => Uri.UnescapeDataString(part[1])).ToArray();
        if (links.Length != 1 || !Uri.TryCreate(links[0], UriKind.Absolute, out _))
            throw new ArgumentException(Localizer.T("This is not an Odyle pairing link. Copy the connection link from Pair Windows app on the dashboard."));
        return Parse(links[0], "https://invalid.invalid");
    }

    private static string NormalizeCode(string value)
    {
        var code = Regex.Replace(value, "[\\s-]", "").ToUpperInvariant();
        if (!Regex.IsMatch(code, "^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{16}$"))
            throw new ArgumentException(Localizer.T("Paste the connection link or 16-character Windows pairing code. Room invites use a different code."));
        return code;
    }
}
