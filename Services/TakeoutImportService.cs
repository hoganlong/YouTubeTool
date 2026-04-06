using System.IO;
using System.Text.Json;

namespace YouTubeTool.Services;

public class TakeoutImportService
{
    // Parses watch-history.json from a Google Takeout export.
    // Returns the set of YouTube video IDs found in the file.
    public HashSet<string> ParseWatchHistory(string filePath)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var stream = File.OpenRead(filePath);
        using var doc = JsonDocument.Parse(stream);

        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("titleUrl", out var urlProp)) continue;
            var url = urlProp.GetString();
            if (string.IsNullOrEmpty(url)) continue;

            var id = ExtractVideoId(url);
            if (!string.IsNullOrEmpty(id))
                ids.Add(id);
        }

        return ids;
    }

    private static string? ExtractVideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return query["v"];
    }
}
