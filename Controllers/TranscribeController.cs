using Microsoft.AspNetCore.Mvc;
using YoutubeExplode;
using YoutubeExplode.Videos.ClosedCaptions;
using System.Net;
using System.Text;

namespace YtTranscribeApi.Controllers;

[ApiController]
[Route("api")]
public class TranscribeController : ControllerBase
{
    private readonly ILogger<TranscribeController> _log;
    private readonly IConfiguration _cfg;

    public TranscribeController(ILogger<TranscribeController> log, IConfiguration cfg)
    {
        _log = log;
        _cfg = cfg;
    }

    public record TranscribeReq(string url, string? preferLanguage);
    public record TranscribeRes(bool ok, string source, string transcript);

    // ✅ CAPTION-ONLY: video indirme yok, openai yok
    [HttpPost("transcribe")]
    public async Task<IActionResult> Transcribe([FromBody] TranscribeReq req, CancellationToken ct)
    {
        _log.LogInformation("START /api/transcribe (caption-only) url={Url}", req.url);

        if (string.IsNullOrWhiteSpace(req.url))
            return BadRequest(new { ok = false, error = "url required" });

        var yt = CreateYoutubeClientWithCookies();

        try
        {
            var lang = NormalizeLang(req.preferLanguage);
            _log.LogInformation("Trying captions only... lang={Lang}", lang);

            var text = await TryGetCaptionAsync(yt, req.url, lang, ct);

            if (!string.IsNullOrWhiteSpace(text))
            {
                _log.LogInformation("Captions FOUND len={Len}", text.Length);
                return Ok(new TranscribeRes(true, "captions", text));
            }

            _log.LogInformation("No captions available");
            return StatusCode(409, new
            {
                ok = false,
                error = "caption_unavailable",
                details = "Bu videonun herkese açık transcript (caption) verisi yok veya sunucudan erişilemiyor.",
                hint = "Cookie ile de alınamadıysa region/IP blok olabilir. En sağlam çözüm upload."
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Caption fetch failed");
            return StatusCode(409, new
            {
                ok = false,
                error = "caption_unavailable",
                details = "Caption verisine erişilemedi (IP/region/age/cookie olabilir).",
                hint = "Cookie işe yaramadıysa muhtemelen region/IP blok. Upload gerekir."
            });
        }
        finally
        {
            _log.LogInformation("END /api/transcribe");
        }
    }

    // (opsiyonel placeholder)
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("transcribe/upload")]
    [Consumes("multipart/form-data")]
    public IActionResult UploadPlaceholder()
        => StatusCode(501, new { ok = false, error = "not_implemented", details = "Upload transcribe bu build’de kapalı." });

    // -------- COOKIE'li YoutubeClient --------

    private YoutubeClient CreateYoutubeClientWithCookies()
    {
        var b64 = _cfg["YT_COOKIES_B64"] ?? Environment.GetEnvironmentVariable("YT_COOKIES_B64");
        if (string.IsNullOrWhiteSpace(b64))
        {
            _log.LogInformation("YT_COOKIES_B64 not set -> using default YoutubeClient()");
            return new YoutubeClient();
        }

        try
        {
            var cookiesTxt = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var jar = new CookieContainer();
            LoadNetscapeCookiesInto(jar, cookiesTxt);

            var handler = new HttpClientHandler
            {
                CookieContainer = jar,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = true
            };

            var http = new HttpClient(handler, disposeHandler: true);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
            );

            _log.LogInformation("YT_COOKIES_B64 loaded -> using cookie-enabled YoutubeClient()");
            return new YoutubeClient(http);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load cookies -> using default YoutubeClient()");
            return new YoutubeClient();
        }
    }

    private static void LoadNetscapeCookiesInto(CookieContainer jar, string cookiesTxt)
    {
        // Netscape cookies.txt format:
        // domain \t flag \t path \t secure \t expiration \t name \t value
        using var sr = new StringReader(cookiesTxt);
        string? line;

        while ((line = sr.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            var parts = line.Split('\t');
            if (parts.Length < 7) continue;

            var domain = parts[0];
            var path = parts[2];
            var secure = parts[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase);
            var name = parts[5];
            var value = parts[6];

            // bazı exporter'lar HttpOnly cookie'leri "#HttpOnly_" ile yazar
            if (domain.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
                domain = domain.Substring("#HttpOnly_".Length);

            if (!domain.StartsWith(".")) domain = "." + domain;

            var cookie = new Cookie(name, value, string.IsNullOrWhiteSpace(path) ? "/" : path, domain)
            {
                Secure = secure,
                HttpOnly = false
            };

            try { jar.Add(cookie); } catch { }
        }
    }

    // -------- helpers --------

    private static string? NormalizeLang(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        lang = lang.Trim().ToLowerInvariant();

        return lang switch
        {
            "turkish" => "tr",
            "türkçe" => "tr",
            "tr" => "tr",
            "english" => "en",
            "ingilizce" => "en",
            "en" => "en",
            _ => lang.Length == 2 ? lang : null
        };
    }

    private static async Task<string?> TryGetCaptionAsync(
        YoutubeClient yt,
        string url,
        string? preferLanguage,
        CancellationToken ct)
    {
        var manifest = await yt.Videos.ClosedCaptions.GetManifestAsync(url, ct);
        if (!manifest.Tracks.Any())
            return null;

        ClosedCaptionTrackInfo? trackInfo = null;

        if (!string.IsNullOrWhiteSpace(preferLanguage))
        {
            trackInfo = manifest.Tracks
                .Where(t => string.Equals(t.Language.Code, preferLanguage, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.IsAutoGenerated ? 0 : 1)
                .FirstOrDefault();
        }

        trackInfo ??= manifest.Tracks
            .OrderByDescending(t => t.IsAutoGenerated ? 0 : 1)
            .FirstOrDefault();

        if (trackInfo is null)
            return null;

        ClosedCaptionTrack track = await yt.Videos.ClosedCaptions.GetAsync(trackInfo, ct);

        var text = string.Join(" ",
            track.Captions.Select(c => c.Text).Where(x => !string.IsNullOrWhiteSpace(x))
        );

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
