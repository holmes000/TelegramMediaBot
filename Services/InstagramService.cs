using System.Net;
using TelegramMediaBot.Models;
using TelegramMediaBot.Services.Instagram;

namespace TelegramMediaBot.Services;

public sealed class IgMediaResult
{
    public string? Caption { get; init; }
    public List<IgMediaItem> Items { get; init; } = [];
    public string? Error { get; init; }

    /// <summary>
    /// True when the tier fully resolved the post, so item types and count are
    /// trustworthy (GraphQL, Cobalt, or the embed page with an explicit image
    /// marker). Non-authoritative image results may be cropped previews or
    /// just the first item of a carousel — only used as a last resort.
    /// </summary>
    public bool Authoritative { get; init; }

    public bool HasError => Error is not null;
}

public sealed class IgMediaItem
{
    public string Type { get; init; } = "";
    public string Url { get; init; } = "";
}

/// <summary>
/// Instagram media extraction orchestrator — no account/cookies required.
///
/// Runs a chain of independent tiers, each maintained or self-healing, so the
/// bot only fails when Instagram is genuinely unreachable:
///   1. Anonymous GraphQL (doc_id auto-discovered from IG's JS bundles)
///   2. Public /embed/captioned/ page
///   3. InstaFix-style embed-fixer services
///   4. Self-hosted Cobalt sidecar, then public Cobalt instances
///
/// A health tracker puts tiers with repeated failures on cooldown so dead
/// tiers don't add latency — but never causes a hard fail: when everything is
/// cooling down, all tiers are tried anyway.
///
/// Stories have no shortcode and always require an authenticated session, so
/// only the Cobalt tier can ever serve them (rarely).
/// </summary>
public sealed class InstagramService
{
    private readonly HttpClient _http;
    private readonly IIgStrategy[] _tiers;
    private readonly TierHealthTracker _health = new();
    private readonly ILogger<InstagramService> _log;

    public InstagramService(BotConfig cfg, ILoggerFactory loggerFactory)
    {
        _log = loggerFactory.CreateLogger<InstagramService>();

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        // 10 second timeout: we want to fail FAST on bad tiers/instances
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Add("Accept", "*/*");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        var docIds = new DocIdProvider(_http, cfg, loggerFactory.CreateLogger<DocIdProvider>());

        _tiers =
        [
            new GraphQlStrategy(_http, docIds, loggerFactory.CreateLogger<GraphQlStrategy>()),
            new EmbedPageStrategy(_http, loggerFactory.CreateLogger<EmbedPageStrategy>()),
            new EmbedFixerStrategy(_http, cfg, loggerFactory.CreateLogger<EmbedFixerStrategy>()),
            new CobaltStrategy(_http, cfg, loggerFactory.CreateLogger<CobaltStrategy>()),
        ];
    }

    public async Task<IgMediaResult> GetMediaInfoAsync(string url, CancellationToken ct)
    {
        var request = new IgRequest(url, IgUrl.ExtractShortcode(url), IgUrl.IsVideoUrl(url));

        // Skip tiers on cooldown — unless that would leave nothing to try.
        var anyAvailable = _tiers.Any(t => _health.IsAvailable(t.Name));
        IgMediaResult? partialFallback = null;

        foreach (var tier in _tiers)
        {
            if (anyAvailable && !_health.IsAvailable(tier.Name))
            {
                _log.LogDebug("Skipping tier {Tier} ({Health})", tier.Name, _health.Describe(tier.Name));
                continue;
            }

            var result = await tier.TryFetchAsync(request, ct);
            if (result is { Items.Count: > 0 })
            {
                // Image-only results from tiers that didn't fully resolve the
                // post may be cropped previews or just a carousel's first image.
                // Hold as a fallback and let an authoritative tier (Cobalt/
                // GraphQL) deliver the full, uncropped album.
                if (!result.Authoritative && !result.Items.Any(i => i.Type == "video"))
                {
                    // Keep the richest fallback (a fixer album beats a single preview).
                    if (partialFallback is null || result.Items.Count > partialFallback.Items.Count)
                        partialFallback = result;
                    _log.LogInformation("Tier {Tier} returned a non-authoritative image-only result ({N} item/s) — held as fallback",
                        tier.Name, result.Items.Count);
                    continue;
                }

                _health.RecordSuccess(tier.Name);
                _log.LogInformation("Extracted {N} items via tier: {Tier}", result.Items.Count, tier.Name);
                return result;
            }

            _health.RecordFailure(tier.Name);
        }

        if (partialFallback is not null)
        {
            _log.LogWarning("No tier fully resolved the post — using partial image fallback ({N} item/s)", partialFallback.Items.Count);
            return partialFallback;
        }

        return new IgMediaResult
        {
            Error = "Instagram blocked all extraction methods (direct API, embed page, embed fixers, and Cobalt). Try again later.",
        };
    }

    /// <summary>
    /// Exercises every tier regardless of health state. Used by the canary to
    /// report which tiers currently work.
    /// </summary>
    public async Task<List<(string Tier, bool Ok, string? Error)>> RunDiagnosticsAsync(string url, CancellationToken ct)
    {
        var request = new IgRequest(url, IgUrl.ExtractShortcode(url), IgUrl.IsVideoUrl(url));
        var results = new List<(string, bool, string?)>();

        foreach (var tier in _tiers)
        {
            try
            {
                var result = await tier.TryFetchAsync(request, ct);
                var ok = result is { Items.Count: > 0 } &&
                         (result.Authoritative ||
                          result.Items.Any(i => i.Type == "video") ||
                          !request.RequireVideo);
                if (ok) _health.RecordSuccess(tier.Name); else _health.RecordFailure(tier.Name);
                results.Add((tier.Name, ok, ok ? null : "no media returned"));
            }
            catch (Exception ex)
            {
                _health.RecordFailure(tier.Name);
                results.Add((tier.Name, false, ex.Message));
            }
        }

        return results;
    }
}
