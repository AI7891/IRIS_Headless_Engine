// =============================================================================
//  Content Renderer — replaces Canva (free tier)
//  ImageSharp for images, QuestPDF for PDFs, FFmpeg for video overlay
// =============================================================================
using InnerShiftLab.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Color = SixLabors.ImageSharp.Color;

namespace InnerShiftLab.Engine;

public interface IContentRenderer
{
    Task<string> RenderImageAsync(string text, string? outPath = null, string palette = "iris-default", int width = 1080, int height = 1080);
    Task<string> RenderPdfAsync(string title, IEnumerable<string> sections, string? outPath = null);
    Task<string> RenderVideoAsync(string text, string backgroundPath, string? outPath = null, int durationSec = 15);
}

public sealed class ContentRenderer : IContentRenderer
{
    private readonly ILogger<ContentRenderer> _log;
    private readonly IWebHostEnvironment _env;
    private readonly string _outputDir;
    public ContentRenderer(ILogger<ContentRenderer> log, IWebHostEnvironment env)
    {
        _log = log; _env = env;
        _outputDir = AppPaths.OutputDir(AppPaths.ResolveRoot(env.ContentRootPath));
    }

    public async Task<string> RenderImageAsync(string text, string? outPath = null, string palette = "iris-default", int width = 1080, int height = 1080)
    {
        outPath ??= Path.Combine(_outputDir, $"iris-{Guid.NewGuid():N}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        // Defaults to 1080x1080 (IG square); the outbox passes per-platform dimensions.
        using var img = new Image<Rgba32>(width, height);
        var (bg, fg) = palette switch
        {
            "iris-dark" => (Color.FromRgb(15, 23, 42), Color.FromRgb(245, 158, 11)),
            "iris-warm" => (Color.FromRgb(255, 247, 237), Color.FromRgb(124, 45, 18)),
            _ => (Color.FromRgb(245, 158, 11), Color.FromRgb(15, 23, 42)),
        };
        img.Mutate(c => c.Fill(bg));

        // Wrap relative to the canvas width so wider/narrower formats fill their line length.
        var lines = WrapText(text, Math.Max(12, 28 * width / 1080));
        var y = height / 5;
        // Resolve a font family: prefer an installed system font, otherwise load one from disk.
        var family = ResolveFontFamily();
        var font = family.CreateFont(48, SixLabors.Fonts.FontStyle.Bold);
        var smallFont = family.CreateFont(28, SixLabors.Fonts.FontStyle.Regular);
        foreach (var line in lines)
        {
            img.Mutate(c => c.DrawText(line, font, fg, new PointF(60, y)));
            y += 80;
        }
        img.Mutate(c => c.DrawText("The Inner Shift Lab · IRIS Method", smallFont, fg, new PointF(60, height - 100)));

        await img.SaveAsPngAsync(outPath);
        _log.LogInformation("Rendered image: {Path}", outPath);
        return outPath;
    }

    public async Task<string> RenderPdfAsync(string title, IEnumerable<string> sections, string? outPath = null)
    {
        outPath ??= Path.Combine(_outputDir, $"iris-{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        QuestPDF.Settings.License = LicenseType.Community;

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, QuestPDF.Infrastructure.Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontFamily(Fonts.Calibri).FontSize(11));
                page.Header().Text("The Inner Shift Lab — IRIS Method").FontSize(10).FontColor("#6B7280");
                page.Content().PaddingVertical(1, QuestPDF.Infrastructure.Unit.Centimetre).Column(col =>
                {
                    col.Item().Text(title).FontSize(22).FontFamily(Fonts.Calibri).SemiBold();
                    col.Item().PaddingTop(12);
                    foreach (var s in sections)
                    {
                        col.Item().PaddingTop(6).Text(s);
                    }
                });
                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
        });
        await Task.Run(() => doc.GeneratePdf(outPath));
        _log.LogInformation("Rendered PDF: {Path}", outPath);
        return outPath;
    }

    public async Task<string> RenderVideoAsync(string text, string backgroundPath, string? outPath = null, int durationSec = 15)
    {
        outPath ??= Path.Combine(_outputDir, $"iris-{Guid.NewGuid():N}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var textFile = Path.Combine(Path.GetDirectoryName(outPath)!, $"text-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(textFile, text);

        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-loop"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(backgroundPath);
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add(
            $"drawtext=fontfile=/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf:textfile={textFile}:fontsize=48:fontcolor=white:x=(w-text_w)/2:y=(h-text_h)/2:box=1:boxcolor=black@0.5");
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(durationSec.ToString());
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add(outPath);

        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
        try { File.Delete(textFile); } catch { }
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"ffmpeg failed: {err}");
        }
        _log.LogInformation("Rendered video: {Path}", outPath);
        return outPath;
    }

    private static SixLabors.Fonts.FontFamily ResolveFontFamily()
    {
        var system = SixLabors.Fonts.SystemFonts.Collection.Families.FirstOrDefault();
        if (system != default) return system;

        // No system fonts (common on trimmed Linux images). Load a bundled/common TTF.
        var candidates = new[]
        {
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        };
        var collection = new SixLabors.Fonts.FontCollection();
        foreach (var path in candidates)
        {
            if (File.Exists(path)) return collection.Add(path);
        }
        throw new InvalidOperationException(
            "No usable font found. Install one (e.g. `apt-get install -y fonts-dejavu-core`) so image rendering can draw text.");
    }

    private static List<string> WrapText(string text, int maxLineLen)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var cur = "";
        foreach (var w in words)
        {
            if ((cur + " " + w).Trim().Length > maxLineLen)
            {
                if (cur.Length > 0) lines.Add(cur);
                cur = w;
            }
            else cur = (cur.Length == 0 ? w : cur + " " + w);
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }
}

