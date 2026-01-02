using Microsoft.AspNetCore.Mvc;
using OpenAI.Audio;
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
    public record TranscribeRes(bool ok, string source, string transcript, string? note = null);

    // ✅ CAPTION-ONLY (cookie ile dener). İndirme yok.
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
                hint = "Audio/video dosyasını /api/transcribe/upload ile gönder."
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

    // ✅ UPLOAD -> OpenAI Whisper (garanti çözüm)
    // Swagger’da görünmesini istiyorsan IgnoreApi kaldırabilirsin ama gerek yok.
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("transcribe/upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(1024L * 1024L * 1024L)]
    public async Task<IActionResult> TranscribeUpload([FromForm] IFormFile file, [FromForm] string? preferLanguage, CancellationToken ct)
    {
        _log.LogInformation("START /api/transcribe/upload file={Name} size={Size} lang={Lang}",
            file?.FileName, file?.Length, preferLanguage);

        if (file == null || file.Length == 0)
            return BadRequest(new { ok = false, error = "file required" });

        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(500, new { ok = false, error = "OPENAI_API_KEY env missing" });

        var lang = NormalizeLang(preferLanguage);

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";
        var tempPath = Path.Combine(Path.GetTempPath(), $"upload-{Guid.NewGuid():N}{ext}");

        try
        {
            _log.LogInformation("Saving upload to {Path}", tempPath);
            await using (var fs = System.IO.File.Create(tempPath))
                await file.CopyToAsync(fs, ct);

            long bytes = 0;
            try { bytes = new FileInfo(tempPath).Length; } catch { }
            _log.LogInformation("Saved bytes={Bytes}. Calling OpenAI whisper-1...", bytes);

            var transcript = await TranscribeLocalFileWithOpenAiAsync(apiKey!, tempPath, file.FileName, lang, ct);

            _log.LogInformation("DONE upload openai len={Len}", transcript.Length);
            return Ok(new TranscribeRes(true, "openai", transcript, "Upload ile OpenAI Whisper kullanıldı."));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FAIL /api/transcribe/upload");
            return StatusCode(500, new { ok = false, error = "transcription_failed", details = ex.Message });
        }
        finally
        {
            try { System.IO.File.Delete(tempPath); } catch { }
            _log.LogInformation("END /api/transcribe/upload");
        }
    }

    // ---------- OpenAI ----------
    private string? GetApiKey()
        => _cfg["OPENAI_API_KEY"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    private async Task<string> TranscribeLocalFileWithOpenAiAsync(
        string apiKey,
        string filePath,
        string fileName,
        string? lang,
        CancellationToken ct)
    {
        var audio = new AudioClient("whisper-1", apiKey);

        await using var fs = System.IO.File.OpenRead(filePath);

        var options = new AudioTranscriptionOptions
        {
            Language = lang
        };

        var res = await audio.TranscribeAudioAsync(fs, fileName, options, ct);

        var text = res.Value.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("OpenAI returned empty transcript.");

        return text!;
    }

    // ---------- Cookie'li YoutubeClient ----------
    private YoutubeClient CreateYoutubeClientWithCookies()
    {
        var b64 = _cfg["YT_COOKIES_B64"] ?? Environment.GetEnvironmentVariable("YT_COOKIES_B64");
        if (string.IsNullOrWhiteSpace(b64))
        {
            _log.LogInformation("YT_COOKIES_B64 not set -> default YoutubeClient()");
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

            _log.LogInformation("YT_COOKIES_B64 loaded -> cookie-enabled YoutubeClient()");
            return new YoutubeClient(http);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load cookies -> default YoutubeClient()");
            return new YoutubeClient();
        }
    }

    private static void LoadNetscapeCookiesInto(CookieContainer jar, string cookiesTxt)
    {
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

            if (domain.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
                domain = domain.Substring("#HttpOnly_".Length);

            if (!domain.StartsWith(".")) domain = "." + domain;
            if (string.IsNullOrWhiteSpace(path)) path = "/";

            var cookie = new Cookie(name, value, path, domain)
            {
                Secure = secure,
                HttpOnly = false
            };

            try { jar.Add(cookie); } catch { }
        }
    }

    // ---------- Helpers ----------
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
