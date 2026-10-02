using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.YtSearch.Services;

public sealed class FailureEntry
{
    [JsonPropertyName("time")]
    public DateTime Time { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}

/// <summary>The last few failed downloads with the reason, shown on the settings page. A client only sees a generic error.</summary>
public class FailureLog
{
    private const int Max = 25;
    private readonly object _lock = new();
    private readonly List<FailureEntry> _entries = new();

    public void Add(TrackResult track, string reason)
    {
        lock (_lock)
        {
            _entries.Insert(0, new FailureEntry { Time = DateTime.UtcNow, Source = track.Source, Title = track.DisplayTitle, Reason = reason });
            if (_entries.Count > Max)
            {
                _entries.RemoveRange(Max, _entries.Count - Max);
            }
        }
    }

    public IReadOnlyList<FailureEntry> Recent()
    {
        lock (_lock)
        {
            return _entries.ToList();
        }
    }
}
