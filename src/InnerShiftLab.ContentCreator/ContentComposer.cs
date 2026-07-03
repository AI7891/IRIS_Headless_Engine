// =============================================================================
//  Stage 4 — content composer
//  Binds the carousel images and the voiceover into a single final output:
//  an mp4 slideshow (each slide shown for an equal share of the narration)
//  plus the raw carousel + audio, packaged for the existing publish backend.
// =============================================================================
using System.Globalization;
using System.Text;
using System.Text.Json;
using InnerShiftLab.Core;

namespace InnerShiftLab.ContentCreator;

public interface IContentComposer
{
    Task<ComposedContent> ComposeAsync(ContentScript script, IReadOnlyList<CarouselSlide> slides,
        string audioPath, CancellationToken ct = default);
}

public sealed class FfmpegContentComposer : IContentComposer
{
    private readonly ILogger<FfmpegContentComposer> _log;
    private readonly string _outputDir;
    private readonly string _scriptsDir;

    public FfmpegContentComposer(ILogger<FfmpegContentComposer> log, IWebHostEnvironment env)
    {
        _log = log;
        var root = AppPaths.ResolveRoot(env.ContentRootPath);
        _outputDir = Path.Combine(AppPaths.OutputDir(root), "creator", "final");
        _scriptsDir = Path.Combine(AppPaths.OutputDir(root), "creator", "scripts");
        Directory.CreateDirectory(_outputDir);
        Directory.CreateDirectory(_scriptsDir);
    }

    public async Task<ComposedContent> ComposeAsync(ContentScript script, IReadOnlyList<CarouselSlide> slides,
        string audioPath, CancellationToken ct = default)
    {
        if (slides.Count == 0) throw new ArgumentException("Cannot compose content without carousel slides.");

        // Persist the script next to the media so every output is traceable to its source script.
        var scriptPath = Path.Combine(_scriptsDir, $"{script.ScriptId}.json");
        await File.WriteAllTextAsync(scriptPath,
            JsonSerializer.Serialize(script, new JsonSerializerOptions { WriteIndented = true }), ct);

        var audioDuration = await ProbeDurationAsync(audioPath, ct);
        var perSlide = Math.Max(1.5, audioDuration / slides.Count);

        // concat demuxer list: each image held for its share of the narration.
        var listPath = Path.Combine(_outputDir, $"{script.ScriptId}-concat.txt");
        var sb = new StringBuilder();
        foreach (var slide in slides.OrderBy(s => s.Index))
        {
            sb.AppendLine($"file '{slide.ImagePath.Replace("'", @"'\''")}'");
            sb.AppendLine($"duration {perSlide.ToString("0.###", CultureInfo.InvariantCulture)}");
        }
        // concat demuxer convention: repeat the last file so its duration is honored.
        sb.AppendLine($"file '{slides[^1].ImagePath.Replace("'", @"'\''")}'");
        await File.WriteAllTextAsync(listPath, sb.ToString(), ct);

        var videoPath = Path.Combine(_outputDir, $"{script.ScriptId}.mp4");
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("concat");
        psi.ArgumentList.Add("-safe"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(listPath);
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(audioPath);
        // Normalize mixed image sizes to the 1080x1080 square used everywhere else in the pipeline.
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add(
            "scale=1080:1080:force_original_aspect_ratio=increase,crop=1080:1080,format=yuv420p");
        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-shortest");
        psi.ArgumentList.Add(videoPath);

        using (var p = System.Diagnostics.Process.Start(psi)!)
        {
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg compose failed: {await stderr}");
        }
        try { File.Delete(listPath); } catch { }

        _log.LogInformation("Composed final output for script {ScriptId}: {Video} ({Duration:0.#}s, {Slides} slides)",
            script.ScriptId, videoPath, audioDuration, slides.Count);

        return new ComposedContent
        {
            ScriptId = script.ScriptId,
            Title = script.Title,
            Caption = BuildCaption(script, slides),
            SlideImagePaths = slides.OrderBy(s => s.Index).Select(s => s.ImagePath).ToArray(),
            AudioPath = audioPath,
            VideoPath = videoPath,
            DurationSeconds = audioDuration,
            ScriptPath = scriptPath,
        };
    }

    private static string BuildCaption(ContentScript script, IReadOnlyList<CarouselSlide> slides)
    {
        var sb = new StringBuilder(script.Caption.Trim());
        if (script.Hashtags.Length > 0)
            sb.Append("\n\n").Append(string.Join(' ', script.Hashtags.Select(h => "#" + h.TrimStart('#'))));
        // Pexels doesn't require attribution, but crediting photographers is good practice.
        var credits = slides.Where(s => s.PhotographerCredit != null).Select(s => s.PhotographerCredit!).Distinct().ToArray();
        if (credits.Length > 0)
            sb.Append("\n\n").Append(string.Join(" · ", credits));
        return sb.ToString();
    }

    private static async Task<double> ProbeDurationAsync(string mediaPath, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        psi.ArgumentList.Add(mediaPath);

        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 || !double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
            throw new InvalidOperationException($"ffprobe failed to read duration of {mediaPath}");
        return duration;
    }
}
