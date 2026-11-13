using System.Text.Json.Serialization;

namespace Kea.Web.Library;

/// <summary>
/// Saved as <c>.kea-meta.json</c> inside a comic's folder after download.
/// Drives both the Recheck button and the scheduled auto-check feature.
/// </summary>
public sealed class KeaMeta
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = "cbz";

    [JsonPropertyName("comicFolders")]
    public bool ComicFolders { get; set; } = true;

    [JsonPropertyName("chapterFolders")]
    public bool ChapterFolders { get; set; }

    /// <summary>The highest chapter number successfully downloaded so far.</summary>
    [JsonPropertyName("lastChapter")]
    public int LastChapter { get; set; }

    /// <summary>
    /// How often to auto-recheck, in days. 0 = off, 7 = weekly, 30 = monthly.
    /// </summary>
    [JsonPropertyName("recheckIntervalDays")]
    public int RecheckIntervalDays { get; set; }

    /// <summary>When the last scheduled recheck was queued.</summary>
    [JsonPropertyName("lastCheckedAt")]
    public DateTimeOffset? LastCheckedAt { get; set; }
}
