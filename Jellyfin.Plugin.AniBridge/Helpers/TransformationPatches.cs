using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AniBridge.Helpers;

/// <summary>
/// Static callbacks for the File Transformation plugin.
/// </summary>
public static class TransformationPatches
{
    private const string PluginName = "AniBridge Downloader";

    /// <summary>
    /// Patches index.html to inject the AniBridge sidebar script.
    /// </summary>
    public static string IndexHtml(PatchRequestPayload content)
    {
        if (string.IsNullOrEmpty(content.Contents))
        {
            return content.Contents ?? string.Empty;
        }

        var scriptTag = $"<script plugin=\"{PluginName}\" src=\"../AniBridge/InjectionScript\" defer></script>";

        // Remove any existing AniBridge script tags (idempotent)
        var regex = new Regex($"<script[^>]*plugin=[\"']{Regex.Escape(PluginName)}[\"'][^>]*>\\s*</script>\\n?");
        var updatedContent = regex.Replace(content.Contents, string.Empty);

        // Inject before </body>
        if (updatedContent.Contains("</body>"))
        {
            return updatedContent.Replace("</body>", $"{scriptTag}\n</body>");
        }

        return updatedContent;
    }

    /// <summary>
    /// Removes the AniWorld script tag from HTML content.
    /// </summary>
    public static string RemoveScript(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content ?? string.Empty;
        }

        var regex = new Regex($"<script[^>]*plugin=[\"']{Regex.Escape(PluginName)}[\"'][^>]*>\\s*</script>\\n?");
        return regex.Replace(content, string.Empty);
    }
}
