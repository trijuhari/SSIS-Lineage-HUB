using Microsoft.JSInterop;
using SsisLineage.UI.Services;

namespace SsisLineage.Web;

/// <summary>
/// Web-mode implementation of <see cref="IFolderPicker"/>.
///
/// Strategy:
///   1. Open a hidden &lt;input type="file" webkitdirectory&gt; via JS so the
///      browser shows a native OS folder-selection dialog.
///   2. The browser returns file paths in the form "FolderName/sub/file.dtsx".
///      We extract the root folder name (e.g. "MyProject").
///   3. Because this is Blazor Server — server and browser are the same machine —
///      we search common user directories for a folder with that name and return
///      its absolute path.
///   4. If not found automatically, we return the folder name alone so the user
///      can see what was selected and correct the path if needed.
/// </summary>
internal sealed class WebFolderPicker(IJSRuntime js) : IFolderPicker
{
    public bool IsSupported => true;   // webkitdirectory works in all modern browsers

    public async Task<string?> PickFolderAsync(string? startingPath = null)
    {
        try
        {
            // Ask the browser to open a folder picker and return the top-level folder name.
            string? folderName = await js.InvokeAsync<string?>("webFolderPicker.pick");

            if (string.IsNullOrWhiteSpace(folderName))
                return null;

            // Try to resolve the absolute path on the server (same machine as browser).
            var resolved = TryResolveAbsolutePath(folderName, startingPath);
            return resolved ?? folderName;   // fallback: return just the name so user sees it
        }
        catch
        {
            return null;
        }
    }

    // ── Resolver ─────────────────────────────────────────────────────────────

    private static string? TryResolveAbsolutePath(string folderName, string? hint)
    {
        try
        {
            // Build candidate search roots — ordered by likelihood.
            var roots = new List<string>();

            // 1. Parent of the previously-used path (most likely same neighbourhood)
            if (!string.IsNullOrWhiteSpace(hint))
            {
                try
                {
                    var parent = Path.GetDirectoryName(hint.TrimEnd('/', '\\'));
                    if (!string.IsNullOrEmpty(parent)) roots.Add(parent);
                }
                catch { }
            }

            // 2. Common user directories
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                {
                    roots.Add(Path.Combine(home, "Documents"));
                    roots.Add(Path.Combine(home, "Desktop"));
                    roots.Add(Path.Combine(home, "Downloads"));
                    roots.Add(home);
                }
            }
            catch { }

            // 3. Absolute short-circuit — if folderName itself looks like a full path
            if (Path.IsPathRooted(folderName) && Directory.Exists(folderName))
                return folderName;

            // Search each root up to 3 levels deep (fast, bounded)
            foreach (var root in roots.Distinct())
            {
                if (!Directory.Exists(root)) continue;

                var match = FindFolder(root, folderName, maxDepth: 3);
                if (match != null) return match;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindFolder(string root, string target, int maxDepth)
    {
        if (maxDepth < 0) return null;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                    return dir;

                var found = FindFolder(dir, target, maxDepth - 1);
                if (found != null) return found;
            }
        }
        catch { /* skip unreadable or protected dirs */ }

        return null;
    }
}
