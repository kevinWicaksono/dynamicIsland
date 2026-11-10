using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DynamicIsland;

internal sealed record TimedLyricLine(TimeSpan Timestamp, string Text);

internal sealed record LyricsResult(string? PlainLyrics, IReadOnlyList<TimedLyricLine> TimedLines);

internal static class LyricsService
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly Regex TimestampPattern = new(
        @"\[(?<minutes>\d{1,3}):(?<seconds>\d{2})(?:\.(?<fraction>\d{1,3}))?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Creates the HTTP client used for lyric lookups and identifies this app to the service.</summary>
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var version = typeof(LyricsService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DynamicIsland", version));
        return client;
    }

    /// <summary>Looks up lyrics by track and artist, then falls back to searching by title.</summary>
    public static async Task<LyricsResult?> GetLyricsAsync(
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(artist) &&
            !string.Equals(artist, "Unknown artist", StringComparison.OrdinalIgnoreCase))
        {
            string url = "https://lrclib.net/api/get" +
                         $"?track_name={Uri.EscapeDataString(title)}" +
                         $"&artist_name={Uri.EscapeDataString(artist)}";
            using var response = await Client.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var match = await response.Content.ReadFromJsonAsync<LrclibResponse>(
                    cancellationToken: cancellationToken);
                var lyrics = ToLyricsResult(match);
                if (lyrics != null) return lyrics;
            }
            else if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }
        }

        string searchUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(title)}";
        var matches = await Client.GetFromJsonAsync<List<LrclibResponse>>(searchUrl, cancellationToken);
        var bestMatch = matches?.FirstOrDefault(match =>
                            string.Equals(match.TrackName?.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase) &&
                            ToLyricsResult(match) != null)
                        ?? matches?.FirstOrDefault(match => ToLyricsResult(match) != null);
        return ToLyricsResult(bestMatch);
    }

    /// <summary>Converts a service response into a result, or null when it contains no lyrics.</summary>
    private static LyricsResult? ToLyricsResult(LrclibResponse? data)
    {
        if (data == null) return null;
        var timedLines = ParseTimedLyrics(data.SyncedLyrics);
        if (string.IsNullOrWhiteSpace(data.PlainLyrics) && timedLines.Count == 0) return null;
        return new LyricsResult(data.PlainLyrics, timedLines);
    }

    /// <summary>Parses timestamped lyric lines and orders them by playback time.</summary>
    private static IReadOnlyList<TimedLyricLine> ParseTimedLyrics(string? syncedLyrics)
    {
        if (string.IsNullOrWhiteSpace(syncedLyrics)) return Array.Empty<TimedLyricLine>();

        var lines = new List<TimedLyricLine>();
        foreach (string sourceLine in syncedLyrics.Split('\n'))
        {
            var matches = TimestampPattern.Matches(sourceLine);
            if (matches.Count == 0) continue;

            string text = TimestampPattern.Replace(sourceLine, "").Trim();
            if (text.Length == 0) continue;

            foreach (Match match in matches)
            {
                if (!int.TryParse(match.Groups["minutes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
                    !int.TryParse(match.Groups["seconds"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
                    continue;

                string fraction = match.Groups["fraction"].Value;
                int milliseconds = fraction.Length == 0
                    ? 0
                    : int.Parse(fraction.PadRight(3, '0'), CultureInfo.InvariantCulture);

                lines.Add(new TimedLyricLine(
                    TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(milliseconds),
                    text));
            }
        }

        return lines.OrderBy(line => line.Timestamp).ToArray();
    }

    private sealed class LrclibResponse
    {
        [JsonPropertyName("trackName")]
        public string? TrackName { get; set; }

        [JsonPropertyName("plainLyrics")]
        public string? PlainLyrics { get; set; }

        [JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; set; }
    }
}
