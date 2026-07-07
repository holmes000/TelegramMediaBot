using System.Text.Json;

namespace TelegramMediaBot.Services.Instagram;

/// <summary>
/// Parses Instagram's shortcode_media JSON shape — shared by the GraphQL
/// response (xdt_shortcode_media) and the embed page's contextJSON/gql_data
/// blob, which use the same structure.
/// </summary>
public static class IgGraphJson
{
    public static (List<IgMediaItem> Items, string? Caption) ParseShortcodeMedia(JsonElement media)
    {
        var items = new List<IgMediaItem>();

        if (media.TryGetProperty("edge_sidecar_to_children", out var sidecar) &&
            sidecar.TryGetProperty("edges", out var edges))
        {
            foreach (var edge in edges.EnumerateArray())
                if (edge.TryGetProperty("node", out var node))
                    AddNode(node, items);
        }
        else
        {
            AddNode(media, items);
        }

        string? caption = null;
        if (media.TryGetProperty("edge_media_to_caption", out var capEdges) &&
            capEdges.TryGetProperty("edges", out var capArr) &&
            capArr.GetArrayLength() > 0 &&
            capArr[0].TryGetProperty("node", out var capNode) &&
            capNode.TryGetProperty("text", out var capText))
        {
            caption = capText.GetString();
        }

        return (items, caption);
    }

    private static void AddNode(JsonElement node, List<IgMediaItem> items)
    {
        var isVideo = node.TryGetProperty("is_video", out var iv) && iv.GetBoolean();

        if (isVideo && node.TryGetProperty("video_url", out var vu) && vu.GetString() is { Length: > 0 } videoUrl)
        {
            items.Add(new IgMediaItem { Type = "video", Url = videoUrl });
        }
        else if (node.TryGetProperty("display_url", out var du) && du.GetString() is { Length: > 0 } displayUrl)
        {
            items.Add(new IgMediaItem { Type = "image", Url = displayUrl });
        }
    }
}
