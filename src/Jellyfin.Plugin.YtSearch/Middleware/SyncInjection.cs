using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.YtSearch.Middleware;

/// <summary>
/// Apps like Manet copy the library into their own database (paged "Items" and "Artists" requests for the whole library) and
/// search that copy, so they never ask the server to search. Recently searched results are put in front of those lists, as if
/// they were the newest items of the library; the paging is kept consistent by shifting the real list behind them.
/// </summary>
internal static class SyncInjection
{
    /// <summary>
    /// For the page at <paramref name="start"/> of <paramref name="limit"/> items, how many injected items it holds, and which
    /// part of the real list to ask Jellyfin for (the real list starts after the <paramref name="injected"/> ones).
    /// </summary>
    internal static (int FromInjected, int RealStart, int? RealLimit) Plan(int start, int? limit, int injected)
    {
        start = Math.Max(0, start);
        var end = limit is { } l ? (long)start + Math.Max(0, l) : long.MaxValue;
        var fromInjected = (int)Math.Max(0, Math.Min(injected, end) - start);
        var realStart = Math.Max(0, start - injected);
        return (fromInjected, realStart, limit is { } lim ? Math.Max(0, lim) - fromInjected : null);
    }

    /// <summary>Puts the injected page in front of Jellyfin's items and counts them in the total.</summary>
    internal static byte[]? Merge(byte[] body, IReadOnlyList<JsonNode> injectedPage, int injectedTotal, int start, int? realLimit)
    {
        if (JsonNode.Parse(body) is not JsonObject root || root["Items"] is not JsonArray real)
        {
            return null;
        }

        var items = real.Select(n => n).Where(n => n is not null).ToList();
        if (realLimit is { } max && items.Count > max)
        {
            items = items.Take(max).ToList();
        }

        var merged = new JsonArray();
        foreach (var n in injectedPage)
        {
            merged.Add(n);
        }

        foreach (var n in items)
        {
            real.Remove(n);
            merged.Add(n);
        }

        root["Items"] = merged;
        root["TotalRecordCount"] = (root["TotalRecordCount"]?.GetValue<int>() ?? items.Count) + injectedTotal;
        root["StartIndex"] = Math.Max(0, start);
        return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
    }
}
