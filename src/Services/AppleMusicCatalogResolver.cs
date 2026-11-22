using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using WindowsLosslessSwitcher.Abstractions;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

public sealed partial class AppleMusicCatalogResolver : IFormatResolver
{
    [System.Text.RegularExpressions.GeneratedRegex("src=\"(?<path>/assets/index[^\"']+\\.js)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex BundleScriptRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"eyJ[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+")]
    private static partial System.Text.RegularExpressions.Regex DeveloperTokenRegex();
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    internal const string DefaultStorefront = "us";

    // Number of search results to request per query attempt. 25 gives enough breadth to find
    // live, deluxe, and remaster variants without requesting an excessively large payload.
    private const int SearchResultLimit = 25;

    // Minimum score required to accept a catalog match and treat the track as an Apple Music catalog
    // (cloud) track. Scoring breakdown: title exact=100, title contains=70; artist exact=50,
    // artist contains=35, artist overlaps=25; album exact=10, album contains=5. A score of 150
    // requires an exact title (100) AND an exact artist (50) — a confident "this is the same track"
    // match. The catalog runs first and is the authoritative cloud-vs-local test: weaker matches fall
    // through to the device-max resolver, so a local file with merely similar metadata is not treated
    // as the catalog track and instead plays at the device's highest format.
    // Bump FormatCacheStore.CatalogResolverVersion when this or related matching logic changes.
    private const int MinimumAcceptedScore = 150;

    private readonly DiagnosticsLogger _logger;
    private readonly FormatCacheStore? _formatCacheStore;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<string?>> _sendAsync;
    private readonly string _storefront;
    private readonly object _tokenSync = new();
    private string? _developerToken;
    private DateTimeOffset _developerTokenExpiresAtUtc;

    public AppleMusicCatalogResolver(
        DiagnosticsLogger logger,
        FormatCacheStore? formatCacheStore = null,
        string? configuredStorefront = null)
        : this(logger, formatCacheStore, SendAsync, ResolveStorefront(configuredStorefront, logger))
    {
    }

    internal AppleMusicCatalogResolver(
        DiagnosticsLogger logger,
        Func<HttpRequestMessage, CancellationToken, Task<string?>> sendAsync,
        string storefront)
        : this(logger, null, sendAsync, storefront)
    {
    }

    internal AppleMusicCatalogResolver(
        DiagnosticsLogger logger,
        FormatCacheStore? formatCacheStore,
        Func<HttpRequestMessage, CancellationToken, Task<string?>> sendAsync,
        string storefront)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storefront);
        _logger = logger;
        _formatCacheStore = formatCacheStore;
        _sendAsync = sendAsync;
        _storefront = storefront.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Resolves the Apple Music storefront: an explicit user override wins, otherwise the OS region,
    /// otherwise "us". Catalog availability and matching differ by storefront, so guessing "us" for a
    /// non-US listener can return different results (or none) for the track they are actually playing.
    /// </summary>
    internal static string ResolveStorefront(string? configuredStorefront, DiagnosticsLogger logger) =>
        ResolveStorefront(configuredStorefront, GetOsRegionCode, logger);

    internal static string ResolveStorefront(
        string? configuredStorefront,
        Func<string?> getOsRegionCode,
        DiagnosticsLogger logger)
    {
        if (!string.IsNullOrWhiteSpace(configuredStorefront))
        {
            var configured = configuredStorefront.Trim().ToLowerInvariant();
            if (IsPlausibleStorefront(configured))
            {
                logger.Info($"Catalog resolver storefront '{configured}' (source: configured override).");
                return configured;
            }

            logger.Warn(
                $"Configured storefront '{configuredStorefront}' is not a two-letter country code; " +
                "falling back to region detection.");
        }

        try
        {
            var region = getOsRegionCode();
            if (!string.IsNullOrWhiteSpace(region))
            {
                var detected = region.Trim().ToLowerInvariant();
                if (IsPlausibleStorefront(detected))
                {
                    logger.Info($"Catalog resolver storefront '{detected}' (source: OS region).");
                    return detected;
                }

                // TwoLetterISORegionName is not always alpha-2: regional formats like es-419
                // yield UN M.49 numeric codes, and invariant globalization yields "IV".
                logger.Warn($"OS region '{region}' is not a usable storefront; falling back to 'us'.");
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Catalog resolver storefront detection from OS region failed: {ex.Message}");
        }

        logger.Info("Catalog resolver storefront 'us' (source: default).");
        return "us";
    }

    // The storefront is interpolated into request paths, so constrain it to exactly two ASCII
    // letters — anything else (M.49 numeric codes, invariant-mode "IV" is shaped fine but a path
    // fragment from a hand-edited settings file is not) must not reach a URL.
    private static bool IsPlausibleStorefront(string value) =>
        value.Length == 2 && value.All(char.IsAsciiLetterLower);

    private static string? GetOsRegionCode() => RegionInfo.CurrentRegion.TwoLetterISORegionName;

    public string Name => "AppleMusicCatalogResolver";

    /// <summary>
    /// True when catalog resolutions are persisted to a format cache store as a side effect.
    /// FormatCacheVerificationService requires a resolver where this is false.
    /// </summary>
    internal bool WritesToCacheStore => _formatCacheStore is not null;

    /// <summary>
    /// The resolved storefront this instance searches. Cache keys are storefront-scoped, so the
    /// cache reader must be constructed with this exact value — a mismatch silently misses on
    /// every lookup while the writer files entries the reader never sees.
    /// </summary>
    internal string Storefront => _storefront;

    public async Task<ResolvedAudioFormat?> ResolveAsync(TrackSnapshot track, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist))
        {
            return null;
        }

        try
        {
            return await ResolveInternalAsync(track, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.Warn($"Catalog resolver failed for {track.UniqueKey}: {ex.Message}");
            return null;
        }
    }

    // How long a cached no-match suppresses the catalog search for a track. Local files never
    // match, but tracks do get added to Apple Music, so the search re-runs occasionally.
    internal static TimeSpan NoMatchRetryInterval { get; set; } = TimeSpan.FromDays(30);

    private async Task<ResolvedAudioFormat?> ResolveInternalAsync(TrackSnapshot track, CancellationToken cancellationToken)
    {
        var normalizedTrack = AppleMusicTrackMetadataNormalizer.NormalizeSnapshot(track);

        // A cached no-match (typically a local file) skips the whole lookup: searching the
        // catalog on every replay of a track that is known not to be there wastes seconds of
        // resolution time and network traffic. The entry expires so newly published tracks are
        // eventually found.
        var cacheKey = _formatCacheStore is null ? null : FormatCacheKey.Create(_storefront, normalizedTrack);
        if (cacheKey is not null &&
            _formatCacheStore!.TryGet(cacheKey, out var cachedEntry) &&
            cachedEntry is { NoMatch: true } &&
            DateTimeOffset.UtcNow - cachedEntry.LastVerifiedAtUtc < NoMatchRetryInterval)
        {
            _logger.Verbose(
                $"Skipping catalog lookup for {normalizedTrack.UniqueKey}: cached no-match from {cachedEntry.LastVerifiedAtUtc:u}.");
            return null;
        }

        var developerToken = await GetDeveloperTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(developerToken))
        {
            return null;
        }

        var bestMatch = await SearchBestMatchAsync(normalizedTrack, developerToken, cancellationToken);
        if (bestMatch is null)
        {
            _logger.Info($"Catalog resolver found no Apple Music match for {normalizedTrack.UniqueKey}.");
            // The search completed against a live catalog and found nothing — a durable outcome
            // worth caching. (Transient failures throw and never reach this point.)
            if (cacheKey is not null)
            {
                _formatCacheStore!.StoreNoMatch(cacheKey);
            }

            return null;
        }

        var matchedSong = bestMatch.Value;
        var song = await GetSongDetailAsync(matchedSong.Id, developerToken, cancellationToken);
        if (song is null)
        {
            // Ambiguous: could be a transient fetch failure rather than "no manifest" — do not
            // cache a negative for it.
            _logger.Info($"Catalog resolver found no enhanced HLS manifest for {normalizedTrack.UniqueKey}.");
            return null;
        }

        var songDetail = song.Value;
        if (string.IsNullOrWhiteSpace(songDetail.EnhancedHlsUrl))
        {
            _logger.Info(
                $"Catalog resolver found no enhanced HLS manifest for {normalizedTrack.UniqueKey} " +
                $"(traits: {(songDetail.AudioTraits.Count == 0 ? "none" : string.Join(", ", songDetail.AudioTraits))}).");
            if (songDetail.AudioTraits.Count == 0 && cacheKey is not null)
            {
                // Nothing says this song has a lossless master — durable.
                _formatCacheStore!.StoreNoMatch(cacheKey);
            }

            // Lossless traits without a manifest URL is an inconsistent response; do not cache a
            // negative for it.
            return null;
        }

        var manifest = await FetchStringAsync(songDetail.EnhancedHlsUrl, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(manifest) ||
            !TryGetHighestLosslessVariant(manifest, out var sampleRateHz, out var bitDepth))
        {
            if (!string.IsNullOrWhiteSpace(manifest))
            {
                _logger.Warn(
                    $"Catalog resolver could not read any lossless variant from the manifest of {songDetail.Id} " +
                    $"({normalizedTrack.UniqueKey}). Manifest excerpt: {DescribeManifestForDiagnostics(manifest)}");
            }

            return null;
        }

        var description = $"Catalog manifest: {bitDepth}/{sampleRateHz / 1000.0:0.###}";
        if (sampleRateHz <= StandardLosslessMaxSampleRateHz && HasTrait(songDetail.AudioTraits, HiResLosslessTrait))
        {
            // A hi-res tier cannot tell us whether the native recording is 88.2, 96,
            // 176.4 or 192 kHz. An incomplete web manifest is not an exact native format.
            // Let local-file resolution or a user-verified correction supply that evidence.
            _logger.Warn($"Catalog {songDetail.Id} is tagged hi-res but its manifest lists only {bitDepth}/{sampleRateHz}; native format remains undetermined.");
            return null;
        }

        _logger.Info(
            $"Catalog resolver matched {songDetail.Id} for {normalizedTrack.UniqueKey}: {bitDepth}/{sampleRateHz} " +
            $"({string.Join(", ", songDetail.AudioTraits)}).");

        var resolved = new ResolvedAudioFormat(
            sampleRateHz,
            bitDepth,
            ResolutionConfidence.Exact,
            AudioFormatSource.CatalogManifest,
            description)
        {
            ObservedAtUtc = DateTimeOffset.UtcNow,
            CatalogSongId = songDetail.Id,
        };

        // Cache successful lookups immediately so results aren't lost if track processing
        // is cancelled (e.g., the user skips the track) before it reaches the coordinator.
        if (cacheKey is not null)
        {
            _formatCacheStore!.Store(cacheKey, resolved);
        }
        return resolved;
    }

    internal async Task<string?> GetDeveloperTokenAsync(CancellationToken cancellationToken)
    {
        lock (_tokenSync)
        {
            if (!string.IsNullOrWhiteSpace(_developerToken) &&
                _developerTokenExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return _developerToken;
            }
        }

        // Every failure below is logged: an unacquirable token disables catalog resolution for all
        // tracks (they land on the conservative fallback), so it must never fail silently — most
        // likely cause is a storefront with no music.apple.com presence.
        var browseHtml = await FetchStringAsync($"https://music.apple.com/{_storefront}/browse", null, cancellationToken);
        if (string.IsNullOrWhiteSpace(browseHtml))
        {
            _logger.Warn(
                $"Developer token unavailable: the '{_storefront}' storefront browse page did not load; " +
                "catalog resolution is disabled until it does.");
            return null;
        }

        var scriptMatch = BundleScriptRegex().Match(browseHtml);
        if (!scriptMatch.Success)
        {
            _logger.Warn($"Developer token unavailable: no bundle script found on the '{_storefront}' browse page.");
            return null;
        }

        var scriptUrl = $"https://music.apple.com{scriptMatch.Groups["path"].Value}";
        var scriptContent = await FetchStringAsync(scriptUrl, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(scriptContent))
        {
            _logger.Warn("Developer token unavailable: the Apple Music bundle script did not load.");
            return null;
        }

        var tokenMatch = System.Text.RegularExpressions.Regex.Match(
            scriptContent,
            @"eyJ[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+");
        if (!tokenMatch.Success)
        {
            _logger.Warn("Developer token unavailable: no token found in the Apple Music bundle script.");
            return null;
        }

        var token = tokenMatch.Value;
        var expiresAtUtc = TryParseJwtExpiryUtc(token, out var parsedExpiryUtc)
            ? parsedExpiryUtc
            : DateTimeOffset.UtcNow.AddHours(6);

        lock (_tokenSync)
        {
            _developerToken = token;
            _developerTokenExpiresAtUtc = expiresAtUtc;
        }

        return token;
    }

    internal async Task<CatalogSongMatch?> SearchBestMatchAsync(
        TrackSnapshot track,
        string developerToken,
        CancellationToken cancellationToken)
    {
        CatalogSongMatch? bestMatch = null;
        _logger.Verbose(
            $"Catalog resolver searching storefront '{_storefront}' for {track.UniqueKey}: " +
            $"title='{track.Title}', artist='{track.Artist}', album='{track.Album}'.");
        foreach (var attempt in BuildSearchAttempts(track))
        {
            var query = Uri.EscapeDataString(attempt.Query);
            var url = $"https://amp-api.music.apple.com/v1/catalog/{_storefront}/search?term={query}&types=songs&limit={SearchResultLimit}";
            var json = await FetchStringAsync(
                url,
                new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {developerToken}",
                    ["Origin"] = "https://music.apple.com",
                },
                cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.Verbose($"Catalog resolver query '{attempt.Name}' (term='{attempt.Query}') returned no response for {track.UniqueKey}.");
                continue;
            }

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("results", out var results) ||
                !results.TryGetProperty("songs", out var songs) ||
                !songs.TryGetProperty("data", out var data))
            {
                _logger.Verbose($"Catalog resolver query '{attempt.Name}' (term='{attempt.Query}') returned 0 song results for {track.UniqueKey}.");
                continue;
            }

            // Score every raw result up front so we can log the top candidates with their scores —
            // including negative (hard-rejected) ones. Without this, a 100% miss is indistinguishable
            // from "Apple returned nothing", which is exactly what we need to tell apart.
            var rawCandidates = new List<(CatalogSongMatch Match, int Score)>();
            foreach (var item in data.EnumerateArray())
            {
                var match = CatalogSongMatch.FromJson(item);
                if (match is null)
                {
                    continue;
                }

                rawCandidates.Add((match.Value, ScoreMatch(track, match.Value.Title, match.Value.Artist, match.Value.Album)));
            }

            var topCandidates = rawCandidates
                .OrderByDescending(candidate => candidate.Score)
                .Take(3)
                .Select(candidate =>
                    $"\"{candidate.Match.Title}\" / \"{candidate.Match.Artist}\" / \"{candidate.Match.Album}\" (score={candidate.Score})");
            _logger.Verbose(
                $"Catalog resolver query '{attempt.Name}' (term='{attempt.Query}') returned {data.GetArrayLength()} result(s) for {track.UniqueKey}; " +
                $"top: {(rawCandidates.Count == 0 ? "none" : string.Join("; ", topCandidates))}.");

            CatalogSongMatch? bestAttemptMatch = null;
            foreach (var (match, score) in rawCandidates)
            {
                if (score < 0)
                {
                    continue;
                }

                var candidate = match with { Score = score };
                if (bestAttemptMatch is null || candidate.CompareTo(bestAttemptMatch.Value) > 0)
                {
                    bestAttemptMatch = candidate;
                }
            }

            if (bestAttemptMatch is null)
            {
                _logger.Verbose($"Catalog resolver query '{attempt.Name}' returned no scored candidates for {track.UniqueKey}.");
                continue;
            }

            _logger.Verbose(
                $"Catalog resolver query '{attempt.Name}' best candidate for {track.UniqueKey}: " +
                $"{bestAttemptMatch.Value.Title} / {bestAttemptMatch.Value.Artist} (score={bestAttemptMatch.Value.Score}).");

            // A same-title recording on another edition can carry a different master. When
            // album metadata is available, let later album-specific queries improve a merely
            // title/artist match before accepting it (issue #3). Retain it as a fallback.
            if (bestAttemptMatch.Value.Score >= MinimumAcceptedScore &&
                (string.IsNullOrWhiteSpace(track.Album) || bestAttemptMatch.Value.Score >= 160))
            {
                return bestAttemptMatch;
            }

            if (bestMatch is null || bestAttemptMatch.Value.CompareTo(bestMatch.Value) > 0)
            {
                bestMatch = bestAttemptMatch;
            }
        }

        if (bestMatch is { Score: >= MinimumAcceptedScore })
        {
            return bestMatch;
        }

        if (bestMatch is not null)
        {
            _logger.Info(
                $"Catalog resolver rejected best candidate for {track.UniqueKey}: " +
                $"{bestMatch.Value.Title} / {bestMatch.Value.Artist} (score={bestMatch.Value.Score}, required={MinimumAcceptedScore}).");
        }

        return null;
    }

    internal async Task<CatalogSongDetail?> GetSongDetailAsync(
        string songId,
        string developerToken,
        CancellationToken cancellationToken)
    {
        var url = $"https://amp-api.music.apple.com/v1/catalog/{_storefront}/songs/{songId}?extend=extendedAssetUrls";
        var json = await FetchStringAsync(
            url,
            new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {developerToken}",
                ["Origin"] = "https://music.apple.com",
            },
            cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
        {
            return null;
        }

        return CatalogSongDetail.FromJson(data[0]);
    }

    internal const string HiResLosslessTrait = "hi-res-lossless";
    private const string LosslessTrait = "lossless";
    private const int StandardLosslessMaxSampleRateHz = 48000;





    private static bool HasTrait(IReadOnlyList<string> traits, string trait) =>
        traits.Any(value => string.Equals(value, trait, StringComparison.OrdinalIgnoreCase));

    internal static bool IsLossyOnly(IReadOnlyList<string> traits) =>
        traits.Count > 0 && !HasTrait(traits, LosslessTrait) && !HasTrait(traits, HiResLosslessTrait);

    /// <summary>
    /// Finds the highest-resolution ALAC variant a master playlist advertises. HLS attribute
    /// order and quoting are not fixed, and a variant's rate/depth may only be carried by its
    /// group id (e.g. <c>audio-alac-stereo-96000-24</c>) on <c>#EXT-X-MEDIA</c> or on the
    /// <c>AUDIO=</c> reference of an <c>#EXT-X-STREAM-INF</c>, or by the base64 JSON of the
    /// <c>com.apple.hls.audioAssetMetadata</c> session data. The previous parser only accepted
    /// <c>#EXT-X-MEDIA:TYPE=AUDIO</c> lines carrying both numeric attributes, and silently fell
    /// back to a lower variant when the hi-res one was described any other way.
    /// </summary>
    internal static bool TryGetHighestLosslessVariant(string manifest, out int sampleRateHz, out int bitDepth)
    {
        var bestRate = 0;
        var bestDepth = 0;

        foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase))
            {
                var attributes = ParseAttributeList(line["#EXT-X-MEDIA:".Length..]);
                if (attributes.TryGetValue("TYPE", out var type) &&
                    !string.Equals(type, "AUDIO", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rate = ParseManifestNumber(attributes, "SAMPLE-RATE");
                var depth = ParseManifestNumber(attributes, "BIT-DEPTH");
                if ((rate is null || depth is null) &&
                    attributes.TryGetValue("GROUP-ID", out var groupId) &&
                    TryParseAlacGroupId(groupId, out var groupRate, out var groupDepth))
                {
                    rate ??= groupRate;
                    depth ??= groupDepth;
                }

                Consider(rate, depth);
            }
            else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                var attributes = ParseAttributeList(line["#EXT-X-STREAM-INF:".Length..]);
                if (attributes.TryGetValue("AUDIO", out var audioGroup) &&
                    TryParseAlacGroupId(audioGroup, out var groupRate, out var groupDepth))
                {
                    Consider(groupRate, groupDepth);
                }
            }
            else if (line.StartsWith("#EXT-X-SESSION-DATA:", StringComparison.OrdinalIgnoreCase))
            {
                var attributes = ParseAttributeList(line["#EXT-X-SESSION-DATA:".Length..]);
                if (attributes.TryGetValue("DATA-ID", out var dataId) &&
                    dataId.Contains("audioAssetMetadata", StringComparison.OrdinalIgnoreCase) &&
                    attributes.TryGetValue("VALUE", out var value))
                {
                    foreach (var (rate, depth) in ReadAlacAssetMetadata(value))
                    {
                        Consider(rate, depth);
                    }
                }
            }
        }

        sampleRateHz = bestRate;
        bitDepth = bestDepth;
        return sampleRateHz > 0 && bitDepth > 0;

        void Consider(int? rate, int? depth)
        {
            if (rate is not { } r || depth is not { } d || r < 8000 || r > 1_536_000 || d < 8 || d > 64)
            {
                return;
            }

            if (r > bestRate || (r == bestRate && d > bestDepth))
            {
                bestRate = r;
                bestDepth = d;
            }
        }
    }

    // Matches stereo ALAC group ids such as "audio-alac-stereo-96000-24" or "alac_96000_24".
    // Atmos, AAC, binaural and downmix groups ("audio-atmos-2768", "audio-stereo-256",
    // "audio-stereo-binaural-256") never match, so they cannot raise the result.
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![a-z])alac(?:[-_]stereo)?[-_](?<rate>\d{5,7})[-_](?<depth>\d{2})(?!\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AlacGroupIdRegex();

    private static bool TryParseAlacGroupId(string groupId, out int sampleRateHz, out int bitDepth)
    {
        sampleRateHz = 0;
        bitDepth = 0;
        var match = AlacGroupIdRegex().Match(groupId);
        return match.Success &&
               int.TryParse(match.Groups["rate"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out sampleRateHz) &&
               int.TryParse(match.Groups["depth"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out bitDepth);
    }

    private static int? ParseManifestNumber(IReadOnlyDictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var raw) &&
        int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>Parses an HLS attribute list (comma-separated, values optionally quoted).</summary>
    internal static Dictionary<string, string> ParseAttributeList(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < text.Length)
        {
            var equals = text.IndexOf('=', index);
            if (equals < 0)
            {
                break;
            }

            var name = text[index..equals].Trim().TrimStart(',').Trim();
            index = equals + 1;
            string value;
            if (index < text.Length && text[index] == '"')
            {
                var closing = text.IndexOf('"', index + 1);
                if (closing < 0)
                {
                    value = text[(index + 1)..];
                    index = text.Length;
                }
                else
                {
                    value = text[(index + 1)..closing];
                    index = closing + 1;
                }
            }
            else
            {
                var comma = text.IndexOf(',', index);
                var end = comma < 0 ? text.Length : comma;
                value = text[index..end].Trim();
                index = end;
            }

            if (index < text.Length && text[index] == ',')
            {
                index++;
            }

            if (name.Length > 0)
            {
                attributes[name] = value;
            }
        }

        return attributes;
    }

    // The session data VALUE is base64 JSON describing each audio rendition. Only objects that
    // identify themselves as ALAC are trusted, so Atmos/AAC metadata can never raise the result.
    private static IEnumerable<(int Rate, int Depth)> ReadAlacAssetMetadata(string value)
    {
        JsonDocument? document = null;
        try
        {
            var bytes = Convert.FromBase64String(PadBase64(value.Trim().Replace('-', '+').Replace('_', '/')));
            document = JsonDocument.Parse(bytes);
        }
        catch
        {
            // Not base64 JSON (or a different shape than expected) — nothing to learn here.
        }

        if (document is null)
        {
            return [];
        }

        using (document)
        {
            var found = new List<(int Rate, int Depth)>();
            Walk(document.RootElement, found, depth: 0);
            return found;
        }

        static void Walk(JsonElement element, List<(int Rate, int Depth)> found, int depth)
        {
            if (depth > 8)
            {
                return;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, found, depth + 1);
                }

                return;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            int? rate = null;
            int? bits = null;
            var isAlac = false;
            foreach (var property in element.EnumerateObject())
            {
                var key = property.Name.Replace("-", string.Empty).Replace("_", string.Empty).ToUpperInvariant();
                switch (key)
                {
                    case "SAMPLERATE":
                        rate = ReadNumber(property.Value);
                        break;
                    case "BITDEPTH":
                        bits = ReadNumber(property.Value);
                        break;
                    case "AUDIOFORMATID" or "CODEC" or "CODECS" or "FORMAT":
                        isAlac |= property.Value.ValueKind == JsonValueKind.String &&
                                  (property.Value.GetString() ?? string.Empty).Contains("alac", StringComparison.OrdinalIgnoreCase);
                        break;
                    default:
                        Walk(property.Value, found, depth + 1);
                        break;
                }
            }

            if (isAlac && rate is { } r && bits is { } b)
            {
                found.Add((r, b));
            }
        }

        static int? ReadNumber(JsonElement value) =>
            value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var number) => number,
                JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null,
            };
    }

    // Lines that describe variants, with URIs (long, signed, useless for diagnosis) removed.
    internal static string DescribeManifestForDiagnostics(string manifest)
    {
        var lines = manifest
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("#EXT-X-MEDIA", StringComparison.OrdinalIgnoreCase) ||
                           line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase) ||
                           line.StartsWith("#EXT-X-SESSION-DATA", StringComparison.OrdinalIgnoreCase))
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, "URI=\"[^\"]*\"", "URI=…"))
            .Select(line => line.Length > 300 ? line[..300] + "…" : line);
        var text = string.Join(" | ", lines);
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }

    private async Task<string?> FetchStringAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }

        return await _sendAsync(request, cancellationToken);
    }

    private static async Task<string?> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WindowsLosslessSwitcher/1.0");
        return client;
    }

    private static bool TryParseJwtExpiryUtc(string token, out DateTimeOffset expiresAtUtc)
    {
        expiresAtUtc = default;
        var segments = token.Split('.');
        if (segments.Length < 2)
        {
            return false;
        }

        try
        {
            var payloadBytes = Convert.FromBase64String(PadBase64(segments[1].Replace('-', '+').Replace('_', '/')));
            using var document = JsonDocument.Parse(payloadBytes);
            if (!document.RootElement.TryGetProperty("exp", out var expElement))
            {
                return false;
            }

            expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expElement.GetInt64());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string PadBase64(string value)
    {
        var remainder = value.Length % 4;
        return remainder == 0 ? value : value.PadRight(value.Length + (4 - remainder), '=');
    }

    private static int ScoreMatch(TrackSnapshot track, string title, string artist, string album)
    {
        var titleScore = ScoreTitle(track.Title, title);
        if (titleScore < 0)
        {
            return -1;
        }

        // Classical tracks arrive with the COMPOSER in the artist field and "<performers> — <album>"
        // packed into the album field, whereas Apple's catalog lists the PERFORMERS as the artist.
        // So score the candidate's artist against either the (composer) artist field or the
        // performers parsed from the album, and score the album against either the whole field or its
        // album part. This stays strictly exact and recording-specific: a different recording has
        // different performers (and album) and will not reach MinimumAcceptedScore.
        var hasPerformers = AppleMusicTrackMetadataNormalizer.TrySplitOnDashSeparator(
            track.Album, out var performers, out var albumPart);

        var artistScore = Math.Max(
            ScoreArtist(track.Artist, artist),
            hasPerformers ? ScoreArtist(performers, artist) : -1);
        if (artistScore < 0)
        {
            return -1;
        }

        var albumScore = Math.Max(
            ScoreAlbum(track.Album, album),
            hasPerformers ? ScoreAlbum(albumPart, album) : 0);

        return titleScore + artistScore + albumScore;
    }

    private static int ScoreTitle(string? expected, string? actual)
    {
        var normalizedExpected = AppleMusicTrackMetadataNormalizer.NormalizeForComparison(expected);
        var normalizedActual = AppleMusicTrackMetadataNormalizer.NormalizeForComparison(actual);
        if (string.IsNullOrEmpty(normalizedExpected) || string.IsNullOrEmpty(normalizedActual))
        {
            return -1;
        }

        if (normalizedExpected == normalizedActual)
        {
            return 100;
        }

        return normalizedExpected.Contains(normalizedActual, StringComparison.Ordinal) ||
               normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal)
            ? 70
            : -1;
    }

    private static int ScoreArtist(string? expected, string? actual)
    {
        var normalizedExpected = AppleMusicTrackMetadataNormalizer.NormalizeForComparison(expected);
        var normalizedActual = AppleMusicTrackMetadataNormalizer.NormalizeForComparison(actual);
        if (string.IsNullOrEmpty(normalizedExpected) || string.IsNullOrEmpty(normalizedActual))
        {
            return -1;
        }

        var expectedCredits = NormalizeArtistCredits(expected);
        var actualCredits = NormalizeArtistCredits(actual);
        if (normalizedExpected == normalizedActual || expectedCredits.SetEquals(actualCredits))
        {
            return 50;
        }

        if (normalizedExpected.Contains(normalizedActual, StringComparison.Ordinal) ||
            normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal))
        {
            return 35;
        }

        return expectedCredits.Overlaps(actualCredits) ? 25 : -1;
    }

    private static HashSet<string> NormalizeArtistCredits(string? artist) =>
        AppleMusicTrackMetadataNormalizer.SplitArtists(artist)
            .Select(credit => credit switch
            {
                // Verified stage-name equivalence, not general transliteration. Keep aliases
                // scoped to complete artist credits so unrelated names cannot match by substring.
                // Primary source: https://www.jvcmusic.co.jp/-/Profile/A027029.html
                "苺りなはむ" => "ichigorinahamu",
                _ => credit,
            })
            .ToHashSet(StringComparer.Ordinal);

    private static int ScoreAlbum(string? expected, string? actual)
    {
        var normalizedExpected = AppleMusicTrackMetadataNormalizer.NormalizeForComparison(expected);
        var normalizedActual = AppleMusicTrackMetadataNormalizer.NormalizeForComparison(actual);
        if (string.IsNullOrEmpty(normalizedExpected) || string.IsNullOrEmpty(normalizedActual))
        {
            return 0;
        }

        if (normalizedExpected == normalizedActual)
        {
            return 10;
        }

        return normalizedExpected.Contains(normalizedActual, StringComparison.Ordinal) ||
               normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal)
            ? 5
            : 0;
    }

    private static IReadOnlyList<CatalogSearchAttempt> BuildSearchAttempts(TrackSnapshot track)
    {
        var normalized = AppleMusicTrackMetadataNormalizer.Normalize(track.Title, track.Artist, track.Album);
        var attempts = new List<CatalogSearchAttempt>();
        AddAttempt("title-primary-artist", $"{normalized.Title} {normalized.PrimaryArtist}");
        AddAttempt("title-artist", $"{normalized.Title} {normalized.Artist}");
        AddAttempt("title-album", $"{normalized.Title} {normalized.Album}");
        AddAttempt("title-only", normalized.Title);
        return attempts;

        void AddAttempt(string name, string query)
        {
            var trimmedQuery = query.Trim();
            if (string.IsNullOrWhiteSpace(trimmedQuery) ||
                attempts.Any(existing => string.Equals(existing.Query, trimmedQuery, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            attempts.Add(new CatalogSearchAttempt(name, trimmedQuery));
        }
    }

    internal readonly record struct CatalogSongMatch(
        string Id,
        string Title,
        string Artist,
        string Album,
        int Score)
    {
        public int CompareTo(CatalogSongMatch other)
        {
            return Score.CompareTo(other.Score);
        }

        public static CatalogSongMatch? FromJson(JsonElement element)
        {
            if (!element.TryGetProperty("id", out var idElement) ||
                !element.TryGetProperty("attributes", out var attributes))
            {
                return null;
            }

            return new CatalogSongMatch(
                idElement.GetString() ?? string.Empty,
                attributes.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
                attributes.TryGetProperty("artistName", out var artistElement) ? artistElement.GetString() ?? string.Empty : string.Empty,
                attributes.TryGetProperty("albumName", out var albumElement) ? albumElement.GetString() ?? string.Empty : string.Empty,
                0);
        }
    }

    internal readonly record struct CatalogSongDetail(
        string Id,
        string EnhancedHlsUrl,
        IReadOnlyList<string> AudioTraits)
    {
        public static CatalogSongDetail? FromJson(JsonElement element)
        {
            if (!element.TryGetProperty("id", out var idElement) ||
                !element.TryGetProperty("attributes", out var attributes))
            {
                return null;
            }

            var traits = Array.Empty<string>();
            if (attributes.TryGetProperty("audioTraits", out var audioTraitsElement) &&
                audioTraitsElement.ValueKind == JsonValueKind.Array)
            {
                traits = audioTraitsElement
                    .EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
            }

            var enhancedHlsUrl = string.Empty;
            if (attributes.TryGetProperty("extendedAssetUrls", out var extendedAssetUrls) &&
                extendedAssetUrls.TryGetProperty("enhancedHls", out var enhancedHlsElement))
            {
                enhancedHlsUrl = enhancedHlsElement.GetString() ?? string.Empty;
            }

            return new CatalogSongDetail(idElement.GetString() ?? string.Empty, enhancedHlsUrl, traits);
        }
    }

    internal readonly record struct CatalogSearchAttempt(string Name, string Query);
}
