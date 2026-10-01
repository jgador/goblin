using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Goblin.Web;

internal sealed class StaticAssets
{
    private readonly Dictionary<string, (byte[] Body, string ContentType)> _files;

    private StaticAssets(Dictionary<string, (byte[] Body, string ContentType)> files) => _files = files;

    public bool Contains(string path) => _files.ContainsKey(path);

    public static async Task<StaticAssets> LoadAsync(string directory)
    {
        var staticFiles = new Dictionary<string, (byte[] Body, string ContentType)>();
        foreach ((string? path, string? file, string? type) in new[]
        {
            ("/", "work/index.html", "text/html; charset=utf-8"),
            ("/api/values.js", "api/values.js", "text/javascript; charset=utf-8"),
            ("/api/client.js", "api/client.js", "text/javascript; charset=utf-8"),
            ("/api/read-scope.js", "api/read-scope.js", "text/javascript; charset=utf-8"),
            ("/app.js", "connection/app.js", "text/javascript; charset=utf-8"),
            ("/codex.js", "connection/codex.js", "text/javascript; charset=utf-8"),
            ("/connection/codex.js", "connection/codex.js", "text/javascript; charset=utf-8"),
            ("/connection/panel.html", "connection/panel.html", "text/html; charset=utf-8"),
            ("/settings/settings.js", "settings/settings.js", "text/javascript; charset=utf-8"),
            ("/settings/github.js", "settings/github.js", "text/javascript; charset=utf-8"),
            ("/settings/slack.js", "settings/slack.js", "text/javascript; charset=utf-8"),
            ("/assets/branding/slack.png", "assets/branding/slack.png", "image/png"),
            ("/settings/system.js", "settings/system.js", "text/javascript; charset=utf-8"),
            ("/settings/timezone.js", "settings/timezone.js", "text/javascript; charset=utf-8"),
            ("/settings/timezone-places.js", "settings/timezone-places.js", "text/javascript; charset=utf-8"),
            ("/settings/timezone-picker.js", "settings/timezone-picker.js", "text/javascript; charset=utf-8"),
            ("/settings/styles.css", "settings/styles.css", "text/css; charset=utf-8"),
            ("/styles.css", "work/styles.css", "text/css; charset=utf-8"),
            ("/work", "work/index.html", "text/html; charset=utf-8"),
            ("/work/app.js", "work/app.js", "text/javascript; charset=utf-8"),
            ("/work/command-submission.js", "work/command-submission.js", "text/javascript; charset=utf-8"),
            ("/work/model-catalog.js", "work/model-catalog.js", "text/javascript; charset=utf-8"),
            ("/work/model-picker.js", "work/model-picker.js", "text/javascript; charset=utf-8"),
            ("/work/model-selection.js", "work/model-selection.js", "text/javascript; charset=utf-8"),
            ("/work/presentation.js", "work/presentation.js", "text/javascript; charset=utf-8"),
            ("/work/surface.js", "work/surface.js", "text/javascript; charset=utf-8"),
            ("/work/styles.css", "work/styles.css", "text/css; charset=utf-8"),
            ("/assets/branding/icon.svg", "assets/branding/icon.svg", "image/svg+xml")
        }) staticFiles.Add(path, (await File.ReadAllBytesAsync(Path.Combine(directory, file)), type));
        return new StaticAssets(staticFiles);
    }

    public void Map(WebApplication app)
    {
        foreach ((string path, (byte[] Body, string ContentType) asset) in _files)
            app.MapGet(path, () => Results.Bytes(asset.Body, asset.ContentType));
    }
}
