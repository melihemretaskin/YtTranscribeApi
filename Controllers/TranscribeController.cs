using Microsoft.AspNetCore.Mvc;
using OpenAI.Audio;
using YoutubeExplode;
using YoutubeExplode.Videos.ClosedCaptions;

namespace YtTranscribeApi.Controllers;

[ApiController]
[Route("api")]
public class TranscribeController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<TranscribeController> _log;

    public TranscribeController(IConfiguration cfg, ILogger<TranscribeController> log)
    {
        _cfg = cfg;
        _log = log;
    }

    public record TranscribeReq(string url, string? preferLanguage, bool forceOpenAi = false);
    public record TranscribeRes(bool ok, string source, string transcript, string? note = null);

    // URL -> caption veya (mümkünse) openai
    [HttpPost("transcribe")]
    public async Task<IActionResult> Transcribe([FromBody] TranscribeReq req, CancellationToken ct)
    {
        _log.LogInformation("START /api/transcribe url={Url}", req.url);

        if (string.IsNullOrWhiteSpace(req.url))
            return BadRequest(new { ok = false, error = "url required" });

        var yt = new YoutubeClient();

        if (!req.forceOpenAi)
        {
            try
            {
                _log.LogInformation("Trying captions... lang={Lang}", req.preferLanguage);
                var captionText = await TryGetCaptionAsync(yt, req.url, req.preferLanguage, ct);

                if (!string.IsNullOrWhiteSpace(captionText))
                {
                    _log.LogInformation("Captions FOUND len={Len}", captionText.Length);
                    return Ok(new TranscribeRes(true, "captions", captionText));
                }

                _log.LogInformation("Captions NOT found");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Caption failed -> fallback to OpenAI");
            }
        }

        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(500, new { ok = false, error = "OPENAI_API_KEY env missing" });

        try
        {
            _log.LogInformation("Downloading audio then OpenAI...");
            var transcript = await TranscribeFromYoutubeWithOpenAiAsync(yt, req.url, apiKey!, req.preferLanguage, ct);

            _log.LogInformation("DONE openai len={Len}", transcript.Length);
            return Ok(new TranscribeRes(true, "openai", transcript, "Caption yok/erişilemedi, OpenAI kullanıldı."));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FAIL /api/transcribe");
            return StatusCode(500, new
            {
                ok = false,
                error = "Transcription failed",
                details = ex.Message,
                hint = "YouTube video server IP/region/age/cookie nedeniyle indirilemiyor olabilir. En sağlam çözüm: /api/transcribe/upload"
            });
        }
        finally
        {
            _log.LogInformation("END /api/transcribe");
        }
    }

    // UPLOAD -> en sağlam (Swagger'dan gizli, swagger patlamasın)
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
            _log.LogInformation("Saved bytes={Bytes}. Calling OpenAI...", bytes);

            var transcript = await TranscribeLocalFileWithOpenAiAsync(apiKey!, tempPath, file.FileName, lang, ct);

            _log.LogInformation("DONE upload openai len={Len}", transcript.Length);
            return Ok(new TranscribeRes(true, "openai", transcript));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FAIL /api/transcribe/upload");
            return StatusCode(500, new { ok = false, error = "Transcription failed", details = ex.Message });
        }
        finally
        {
            try { System.IO.File.Delete(tempPath); } catch { }
            _log.LogInformation("END /api/transcribe/upload");
        }
    }

    // ---------- helpers ----------
    private string? GetApiKey()
        => _cfg["OPENAI_API_KEY"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

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

    private async Task<string?> TryGetCaptionAsync(YoutubeClient yt, string url, string? preferLanguage, CancellationToken ct)
    {
        var manifest = await yt.Videos.ClosedCaptions.GetManifestAsync(url, ct);
        _log.LogInformation("Caption tracks count={Count}", manifest.Tracks.Count);

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

        _log.LogInformation("Selected caption track lang={Lang} auto={Auto}", trackInfo.Language.Code, trackInfo.IsAutoGenerated);

        ClosedCaptionTrack track = await yt.Videos.ClosedCaptions.GetAsync(trackInfo, ct);

        var text = string.Join(" ",
            track.Captions.Select(c => c.Text).Where(x => !string.IsNullOrWhiteSpace(x))
        );

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private async Task<string> TranscribeFromYoutubeWithOpenAiAsync(
        YoutubeClient yt,
        string url,
        string apiKey,
        string? preferLanguage,
        CancellationToken ct)
    {
        var lang = NormalizeLang(preferLanguage);

        _log.LogInformation("Fetching stream manifest...");
        var streamManifest = await yt.Videos.Streams.GetManifestAsync(url, ct);

        var audioStreamInfo = streamManifest
            .GetAudioOnlyStreams()
            .OrderByDescending(s => s.Bitrate)
            .FirstOrDefault();

        if (audioStreamInfo is null)
            throw new InvalidOperationException("No audio stream found.");

        _log.LogInformation("Selected audio container={Container} bitrate={Bitrate} size={Size}",
            audioStreamInfo.Container.Name, audioStreamInfo.Bitrate, audioStreamInfo.Size);

        var tempPath = Path.Combine(Path.GetTempPath(), $"yt-audio-{Guid.NewGuid():N}.{audioStreamInfo.Container.Name}");

        _log.LogInformation("Downloading audio to {Path}", tempPath);

        await using (var fs = System.IO.File.OpenWrite(tempPath))
        {
            await yt.Videos.Streams.CopyToAsync(audioStreamInfo, fs, progress: null, cancellationToken: ct);
        }

        long bytes = 0;
        try { bytes = new FileInfo(tempPath).Length; } catch { }
        _log.LogInformation("Downloaded bytes={Bytes}", bytes);

        try
        {
            return await TranscribeLocalFileWithOpenAiAsync(apiKey, tempPath, Path.GetFileName(tempPath), lang, ct);
        }
        finally
        {
            try { System.IO.File.Delete(tempPath); } catch { }
        }
    }

    private async Task<string> TranscribeLocalFileWithOpenAiAsync(
        string apiKey,
        string filePath,
        string fileName,
        string? lang,
        CancellationToken ct)
    {
        var audio = new AudioClient("whisper-1", apiKey);

        await using var fileStream = System.IO.File.OpenRead(filePath);

        var options = new AudioTranscriptionOptions { Language = lang };

        var res = await audio.TranscribeAudioAsync(fileStream, fileName, options, ct);

        var text = res.Value.Text?.Trim();
        _log.LogInformation("OpenAI textLen={Len}", text?.Length ?? 0);

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("OpenAI returned empty transcript.");

        return text!;
    }
}
