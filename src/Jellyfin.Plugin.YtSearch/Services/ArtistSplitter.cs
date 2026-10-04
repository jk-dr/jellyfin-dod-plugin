using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.YtSearch.Services;

/// <summary>
/// Splits a credit like "Daft Punk, Pharrell Williams & Nile Rodgers" into separate artists, so each gets its own artist
/// page, while keeping the names of bands that contain "&" or "," whole ("Simon &amp; Garfunkel").
/// </summary>
public static class ArtistSplitter
{
    private static readonly string[] Whole =
    {
        "Simon & Garfunkel", "Earth, Wind & Fire", "Tyler, The Creator", "Florence + The Machine", "Hall & Oates", "Years & Years",
        "Of Mice & Men", "Brooks & Dunn", "Sly & the Family Stone", "Mumford & Sons", "Bob Marley & The Wailers", "Crosby, Stills & Nash",
        "Crosby, Stills, Nash & Young", "Macklemore & Ryan Lewis", "Big Brother & the Holding Company", "Katrina & the Waves",
        "Martha & the Vandellas", "Echo & the Bunnymen", "Siouxsie & the Banshees", "Marina & the Diamonds", "Emerson, Lake & Palmer",
        "Derek & the Dominos", "Kool & the Gang", "Prince & The Revolution", "Tom Petty & the Heartbreakers", "Huey Lewis & the News",
        "Gerry & the Pacemakers", "Hootie & the Blowfish",
    };

    private static readonly Regex Separator = new(
        @"\s*(?:,|&|×)\s*|\s+(?:x|vs\.?|feat\.?|ft\.?|featuring)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Placeholder = new("\u0001(\\d+)\u0001", RegexOptions.Compiled);

    /// <summary>Always returns at least one entry (the credit itself when it can't be split, or an empty string for no credit).</summary>
    public static IReadOnlyList<string> Split(string? credit)
    {
        var text = (credit ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new[] { string.Empty };
        }

        // Park known band names behind placeholders so their "&" or "," is not treated as a separator.
        var parked = new List<string>();
        foreach (var name in Whole)
        {
            var match = Regex.Match(text, Regex.Escape(name), RegexOptions.IgnoreCase);
            if (match.Success)
            {
                text = text.Remove(match.Index, match.Length).Insert(match.Index, $"\u0001{parked.Count}\u0001");
                parked.Add(match.Value);
            }
        }

        var parts = Separator.Split(text)
            .Select(p => Placeholder.Replace(p, m => parked[int.Parse(m.Groups[1].Value)]).Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return parts.Count > 0 ? parts : new[] { (credit ?? string.Empty).Trim() };
    }
}
