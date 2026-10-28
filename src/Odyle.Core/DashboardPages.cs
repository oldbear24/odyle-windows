using System.Text.RegularExpressions;

namespace Odyle.Core;

// Links to the dashboard's pages (#70, #71). From dashboard 0.49.0 every page has its own path
// (/tasks, /account, /armory/eu/2304/…), served with the page's own title and link preview, and the
// backend marks its page with <meta name="odyle-routes" content="paths">. An older backend answers
// any path with its page, whose files are relative to it, so a deep path loads blank there: those
// links keep the #/ form. Invite links (#/join/<code>) are never built here: a code never reaches a server.
public static class DashboardPages
{
    private static readonly Regex Mark = new("""<meta\s+name\s*=\s*["']odyle-routes["']\s+content\s*=\s*["']paths["']""", RegexOptions.IgnoreCase);
    // Whether the backend's page (its answer to GET /) says it serves paths.
    public static bool ServesPaths(string page) => Mark.IsMatch(page);
    // The address of a page (path like "/tasks", "/" for the start page) on the server at home.
    public static string Url(string home, string path, bool paths)
    {
        home = home.TrimEnd('/'); path = "/" + path.TrimStart('/');
        return path == "/" ? home + "/" : paths ? home + path : home + "/#" + path;
    }
}
