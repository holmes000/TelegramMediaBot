using System.Text.RegularExpressions;
using TelegramMediaBot.Models;

namespace TelegramMediaBot.Services.Instagram;

/// <summary>
/// Supplies the GraphQL doc_id for the PolarisPostActionLoadPostQuery.
/// Instagram rotates doc_ids every few weeks as an anti-scraping measure, so a
/// hardcoded value silently breaks. When the GraphQL tier starts failing, this
/// provider re-discovers the current doc_id from Instagram's own JS bundles
/// (linked from the anonymously-reachable embed page) and persists it across
/// restarts. Priority: config override → discovered value → hardcoded fallback.
/// </summary>
public sealed partial class DocIdProvider
{
    public const string FallbackDocId = "8845758582119845";
    private const int MaxBundlesToScan = 10;

    private readonly HttpClient _http;
    private readonly BotConfig _cfg;
    private readonly ILogger _log;
    private readonly string _cacheFile = Path.Combine("data", "ig_docid.txt");
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);

    private string? _discovered;
    private int _consecutiveFailures;
    private DateTime _lastDiscoveryAttempt = DateTime.MinValue;

    public DocIdProvider(HttpClient http, BotConfig cfg, ILogger log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;

        try
        {
            if (File.Exists(_cacheFile))
            {
                var cached = File.ReadAllText(_cacheFile).Trim();
                if (cached.Length > 0 && cached.All(char.IsDigit))
                {
                    _discovered = cached;
                    _log.LogInformation("Loaded cached Instagram doc_id {DocId}", cached);
                }
            }
        }
        catch { }
    }

    public string CurrentDocId =>
        !string.IsNullOrWhiteSpace(_cfg.IgDocId) ? _cfg.IgDocId : _discovered ?? FallbackDocId;

    public void ReportSuccess() => Interlocked.Exchange(ref _consecutiveFailures, 0);

    /// <summary>
    /// Called when GraphQL answers HTTP 200 but without media — the signature
    /// of a stale doc_id. (HTTP-level blocks must NOT be reported here; no
    /// doc_id fixes an IP block.) Two strikes in a row kick off re-discovery
    /// in the background — throttled to once per hour — so user requests never
    /// wait on the bundle scan.
    /// </summary>
    public void ReportFailure(string shortcode)
    {
        if (Interlocked.Increment(ref _consecutiveFailures) < 2) return;
        if (DateTime.UtcNow - _lastDiscoveryAttempt < TimeSpan.FromHours(1)) return;

        _ = Task.Run(async () =>
        {
            await _discoveryLock.WaitAsync();
            try
            {
                if (DateTime.UtcNow - _lastDiscoveryAttempt < TimeSpan.FromHours(1)) return;
                _lastDiscoveryAttempt = DateTime.UtcNow;
                await DiscoverAsync(shortcode, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Background doc_id discovery failed: {Msg}", ex.Message);
            }
            finally
            {
                _discoveryLock.Release();
            }
        });
    }

    private async Task DiscoverAsync(string shortcode, CancellationToken ct)
    {
        _log.LogInformation("Attempting to discover current Instagram doc_id from JS bundles...");
        try
        {
            using var response = await _http.GetAsync(
                $"https://www.instagram.com/p/{shortcode}/embed/captioned/", ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("doc_id discovery: embed page returned HTTP {Code}", (int)response.StatusCode);
                return;
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var scanned = 0;

            foreach (var bundleUrl in ExtractBundleUrls(html))
            {
                if (++scanned > MaxBundlesToScan) break;

                string js;
                try
                {
                    js = await _http.GetStringAsync(bundleUrl, ct);
                }
                catch
                {
                    continue;
                }

                if (TryExtractDocId(js) is { } docId)
                {
                    if (docId != CurrentDocId)
                        _log.LogInformation("Discovered new Instagram doc_id {DocId} (was {Old})", docId, CurrentDocId);

                    _discovered = docId;
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    try { File.WriteAllText(_cacheFile, docId); } catch { }
                    return;
                }
            }

            _log.LogWarning("doc_id discovery: scanned {N} bundles, no PolarisPostActionLoadPostQuery id found", scanned);
        }
        catch (Exception ex)
        {
            _log.LogWarning("doc_id discovery failed: {Msg}", ex.Message);
        }
    }

    /// <summary>Script bundle URLs referenced by an Instagram page, in document order.</summary>
    public static IEnumerable<string> ExtractBundleUrls(string html) =>
        BundleUrlRegex().Matches(html).Select(m => m.Groups[1].Value).Distinct();

    /// <summary>
    /// Finds the doc_id for PolarisPostActionLoadPostQuery inside a JS bundle.
    /// Instagram ships it as a relay-operation module exporting the id string;
    /// the exact module shape shifts between builds, so several patterns are tried.
    /// </summary>
    public static string? TryExtractDocId(string js)
    {
        foreach (var regex in (Regex[])[DocIdRelayModuleRegex(), DocIdParamsIdFirstRegex(), DocIdParamsNameFirstRegex()])
        {
            var match = regex.Match(js);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    [GeneratedRegex("""(?:src|href)="(https://static\.cdninstagram\.com/rsrc\.php/[^"]+\.js[^"]*)""")]
    private static partial Regex BundleUrlRegex();

    // __d("PolarisPostActionLoadPostQuery_instagramRelayOperation",[],(function(...){e.exports="8845758582119845"}),null)
    [GeneratedRegex(@"PolarisPostActionLoadPostQuery_instagramRelayOperation[\s\S]{0,300}?exports\s*=\s*""(\d+)""")]
    private static partial Regex DocIdRelayModuleRegex();

    // params:{id:"8845758582119845",...,name:"PolarisPostActionLoadPostQuery"
    [GeneratedRegex(@"id:""(\d+)""[\s\S]{0,300}?name:""PolarisPostActionLoadPostQuery""")]
    private static partial Regex DocIdParamsIdFirstRegex();

    // name:"PolarisPostActionLoadPostQuery",...,id:"8845758582119845"
    [GeneratedRegex(@"PolarisPostActionLoadPostQuery""[\s\S]{0,300}?id:""(\d+)""")]
    private static partial Regex DocIdParamsNameFirstRegex();
}
