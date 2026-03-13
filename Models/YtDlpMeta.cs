using System.Text.Json.Serialization;

namespace TelegramMediaBot.Models;

/// <summary>Metadata extracted by yt-dlp --dump-json.</summary>
public sealed class YtDlpMeta
{
    [JsonPropertyName("id")]           public string Id { get; set; } = "";
    [JsonPropertyName("title")]        public string? Title { get; set; }
    [JsonPropertyName("uploader")]     public string? Uploader { get; set; }
    [JsonPropertyName("duration")]     public double? Duration { get; set; }
    [JsonPropertyName("ext")]          public string? Extension { get; set; }
    [JsonPropertyName("_type")]        public string? Type { get; set; }
    [JsonPropertyName("entries")]      public List<YtDlpMeta>? Entries { get; set; }
    [JsonPropertyName("formats")]      public List<YtDlpFormat>? Formats { get; set; }
}

public sealed class YtDlpFormat
{
    [JsonPropertyName("format_id")]  public string? FormatId { get; set; }
    [JsonPropertyName("ext")]        public string? Extension { get; set; }
    [JsonPropertyName("vcodec")]     public string? VideoCodec { get; set; }
    [JsonPropertyName("acodec")]     public string? AudioCodec { get; set; }
}
