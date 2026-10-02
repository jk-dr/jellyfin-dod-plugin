using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>
/// Orders results from all sources by how well they match what the user typed, instead of source by source.
/// Mixes words of the query across title and artist, rewards exact/prefix matches and official audio,
/// penalises covers/remixes/mixes the query didn't ask for, and uses each source's own order as a tie-break.
/// </summary>
public static class RelevanceRanker
{
    private static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal)
    {
        "cover", "remix", "karaoke", "slowed", "reverb", "nightcore", "sped", "8d", "instrumental", "mashup", "bootleg",
        "tutorial", "reaction", "parody", "acoustic", "live", "edit", "bass", "boosted", "tiktok", "mix", "playlist", "hour", "hours",
    };

    public static IReadOnlyList<TrackResult> Rank(string query, IReadOnlyList<TrackResult> results)
    {
        var q = Tokens(query);
        if (q.Count == 0 || results.Count < 2)
        {
            return results;
        }

        var normalizedQuery = string.Join(' ', Tokens(query));
        return results
            .Select((r, i) => (Result: r, Index: i, Score: Score(q, normalizedQuery, r, RankInSource(results, r))))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Result)
            .ToList();
    }

    internal static double Score(IReadOnlyList<string> q, string normalizedQuery, TrackResult r, int rankInSource)
    {
        var title = Tokens(r.Title);
        var artist = Tokens(r.Artist);
        var qSet = q.ToHashSet();
        var titleSet = title.ToHashSet();
        var both = titleSet.Union(artist).ToHashSet();

        var titleCoverage = qSet.Count(titleSet.Contains) / (double)qSet.Count;
        var bothCoverage = qSet.Count(both.Contains) / (double)qSet.Count;
        var score = (0.6 * titleCoverage) + (0.8 * bothCoverage);

        var normalizedTitle = string.Join(' ', title);
        var normalizedBoth = string.Join(' ', artist.Concat(title));
        if (normalizedTitle == normalizedQuery)
        {
            score += 0.4;
        }
        else if (normalizedTitle.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            score += 0.2;
        }
        else if (normalizedTitle.Contains(normalizedQuery, StringComparison.Ordinal) || normalizedBoth.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 0.15;
        }

        // Words the user did not type that signal a different version of the song.
        var noise = titleSet.Count(w => NoiseWords.Contains(w) && !qSet.Contains(w));
        score -= Math.Min(noise, 3) * 0.25;

        // A long title around the query is usually a compilation, not the song itself.
        score -= Math.Min(title.Count(w => !qSet.Contains(w)), 15) * 0.01;

        if (r.Artist.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase) || titleSet.Contains("official"))
        {
            score += 0.08;
        }

        if (r.DurationSeconds > 20 * 60 && !qSet.Contains("mix"))
        {
            score -= 0.2;
        }
        else if (r.DurationSeconds < 30)
        {
            score -= 0.1;
        }

        return score - (0.03 * rankInSource);
    }

    private static int RankInSource(IReadOnlyList<TrackResult> all, TrackResult r)
    {
        var rank = 0;
        foreach (var other in all)
        {
            if (ReferenceEquals(other, r))
            {
                return rank;
            }

            if (other.Source == r.Source)
            {
                rank++;
            }
        }

        return rank;
    }

    /// <summary>Lowercase words without accents or punctuation.</summary>
    internal static List<string> Tokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
