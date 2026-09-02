using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using HlslPerf.Core;
using HlslPerf.GpuDriven;

namespace HlslPerf.GpuDrivenDemo;

internal static class CrowdVfxComposer
{
    private const int CanvasWidth = 1280;
    private const int CanvasHeight = 720;
    private const int OutputFrameCount = 48;
    private const int FramesPerSecond = 12;

    public static CrowdVisualArtifacts WriteBudgetCrossing(
        DeviceFingerprint device,
        CrowdLevelEvidence evidence,
        BudgetChoice budget,
        ReadOnlyMemory<byte> actualGpuAtlas,
        string outputDirectory)
    {
        string actualHash = ContentHash.Sha256(actualGpuAtlas.Span);
        if (!string.Equals(actualHash, evidence.AtlasSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The retained GPU atlas does not match the correctness-gated SHA-256.");
        if (evidence.BaselineSamplesMilliseconds.Count == 0 || evidence.SelectedSamplesMilliseconds.Count == 0)
            throw new InvalidDataException("Crowd/VFX visual composition requires recorded GPU samples.");

        Directory.CreateDirectory(outputDirectory);
        string framesDirectory = Path.Combine(outputDirectory, "frames");
        Directory.CreateDirectory(framesDirectory);
        using FrameAtlas frames = new(actualGpuAtlas, evidence.Width, evidence.Height, evidence.FrameCount);
        using Font titleFont = new("Segoe UI", 25, FontStyle.Bold, GraphicsUnit.Pixel);
        using Font subtitleFont = new("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel);
        using Font sectionFont = new("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
        using Font metricFont = new("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
        using Font bodyFont = new("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
        using Font smallFont = new("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using Font badgeFont = new("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
        using Font monoFont = new("Consolas", 12, FontStyle.Regular, GraphicsUnit.Pixel);

        const int deadlinesPerOutputFrame = 4;
        double speedup = evidence.Speedup;
        for (int outputFrame = 0; outputFrame < OutputFrameCount; ++outputFrame)
        {
            int submitted = (outputFrame + 1) * deadlinesPerOutputFrame;
            FrameCadenceSnapshot baselineCadence = FrameDeadlineReplay.Replay(
                evidence.BaselineSamplesMilliseconds,
                budget.Milliseconds,
                submitted,
                evidence.FrameCount);
            FrameCadenceSnapshot selectedCadence = FrameDeadlineReplay.Replay(
                evidence.SelectedSamplesMilliseconds,
                budget.Milliseconds,
                submitted,
                evidence.FrameCount);

            using Bitmap canvas = new(CanvasWidth, CanvasHeight, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(canvas);
            Configure(graphics);
            DrawBackground(graphics);
            using SolidBrush primary = new(Color.FromArgb(239, 246, 255));
            using SolidBrush muted = new(Color.FromArgb(148, 163, 184));
            using SolidBrush baselineBrush = new(Color.FromArgb(255, 184, 88));
            using SolidBrush optimizedBrush = new(Color.FromArgb(63, 231, 164));

            graphics.DrawString("GPU-DRIVEN CROWD/VFX · SAME PIXELS, DIFFERENT DEADLINES", titleFont, primary, 34, 24);
            string budgetLabel = budget.Kind == "requested" ? "requested deadline" : "measured-fit deadline";
            graphics.DrawString(
                $"{device.AdapterName} · {evidence.Level.ToUpperInvariant()} · {evidence.AgentCount:N0} agents · " +
                $"{evidence.MeanVisibleCount:N0} mean visible · {budget.Hertz:0.#} Hz {budgetLabel}",
                subtitleFont,
                muted,
                36,
                64);

            RectangleF baselinePanel = new(34, 116, 594, 334);
            RectangleF optimizedPanel = new(652, 116, 594, 334);
            DrawPanel(graphics, frames[baselineCadence.SourceFrame], baselinePanel, Color.FromArgb(255, 172, 72));
            DrawPanel(graphics, frames[selectedCadence.SourceFrame], optimizedPanel, Color.FromArgb(63, 231, 164));
            graphics.DrawString("MATERIALIZED PIPELINE", sectionFont, baselineBrush, 40, 89);
            graphics.DrawString("FUSED + LDS PIPELINE", sectionFont, optimizedBrush, 658, 89);
            DrawFrameBadge(graphics, baselinePanel, baselineCadence, badgeFont, Color.FromArgb(255, 172, 72));
            DrawFrameBadge(graphics, optimizedPanel, selectedCadence, badgeFont, Color.FromArgb(63, 231, 164));

            graphics.DrawString(
                "flags → hierarchy scan → scatter → global tile atomics",
                monoFont,
                primary,
                38,
                458);
            graphics.DrawString(
                "producer→look-back→scatter → replicated LDS tile bins",
                monoFont,
                primary,
                656,
                458);

            DrawMetricCard(
                graphics,
                new RectangleF(34, 488, 594, 91),
                evidence.BaselineMedianMilliseconds,
                evidence.BaselineP95Milliseconds,
                evidence.BaselineCoefficientOfVariation,
                1,
                metricFont,
                bodyFont,
                primary,
                muted,
                Color.FromArgb(255, 172, 72));
            DrawMetricCard(
                graphics,
                new RectangleF(652, 488, 594, 91),
                evidence.SelectedMedianMilliseconds,
                evidence.SelectedP95Milliseconds,
                evidence.SelectedCoefficientOfVariation,
                speedup,
                metricFont,
                bodyFont,
                primary,
                muted,
                Color.FromArgb(63, 231, 164));

            DrawCadenceStrip(
                graphics,
                new RectangleF(34, 590, 594, 52),
                baselineCadence,
                submitted,
                smallFont,
                Color.FromArgb(255, 172, 72));
            DrawCadenceStrip(
                graphics,
                new RectangleF(652, 590, 594, 52),
                selectedCadence,
                submitted,
                smallFont,
                Color.FromArgb(63, 231, 164));

            graphics.DrawString(
                $"Byte-identical actual GPU atlas · SHA-256 {evidence.AtlasSha256[..12]}… · each side replays its recorded GPU samples",
                bodyFont,
                optimizedBrush,
                36,
                655);
            graphics.DrawString(
                "Same agents, resolution, tile lists and raster result · no synthetic delay / quality cut · upload, readback and media composition excluded",
                smallFont,
                muted,
                36,
                683);

            canvas.Save(Path.Combine(framesDirectory, $"frame-{outputFrame:D3}.png"), ImageFormat.Png);
        }

        string stem = $"crowd-vfx-{evidence.Level}-budget-crossing";
        string gifPath = Path.Combine(outputDirectory, stem + ".gif");
        string mp4Path = Path.Combine(outputDirectory, stem + ".mp4");
        EncodeGif(framesDirectory, gifPath);
        EncodeMp4(framesDirectory, mp4Path);
        return new CrowdVisualArtifacts(gifPath, mp4Path, framesDirectory);
    }

    private static void Configure(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    private static void DrawBackground(Graphics graphics)
    {
        using LinearGradientBrush gradient = new(
            new Rectangle(0, 0, CanvasWidth, CanvasHeight),
            Color.FromArgb(5, 14, 28),
            Color.FromArgb(13, 26, 48),
            LinearGradientMode.ForwardDiagonal);
        graphics.FillRectangle(gradient, 0, 0, CanvasWidth, CanvasHeight);
    }

    private static void DrawPanel(Graphics graphics, Bitmap source, RectangleF destination, Color accent)
    {
        using SolidBrush card = new(Color.FromArgb(12, 25, 46));
        using Pen border = new(Color.FromArgb(115, accent), 2);
        graphics.FillRoundedRectangle(card, destination, 14);
        RectangleF imageDestination = new(
            destination.X + 8,
            destination.Y + 8,
            destination.Width - 16,
            destination.Height - 16);
        graphics.DrawImage(source, imageDestination);
        graphics.DrawRoundedRectangle(border, destination, 14);
    }

    private static void DrawFrameBadge(
        Graphics graphics,
        RectangleF panel,
        FrameCadenceSnapshot cadence,
        Font font,
        Color accent)
    {
        string text = cadence.Backlog == 0
            ? $"LIVE · FRAME {cadence.Completed}"
            : $"QUEUE +{cadence.Backlog} · FRAME {cadence.Completed}";
        SizeF size = graphics.MeasureString(text, font);
        RectangleF badge = new(panel.Right - size.Width - 34, panel.Y + 20, size.Width + 18, size.Height + 8);
        using SolidBrush background = new(Color.FromArgb(cadence.Backlog == 0 ? 190 : 220, 8, 17, 29));
        using SolidBrush foreground = new(accent);
        using Pen border = new(Color.FromArgb(170, accent), 1);
        graphics.FillRoundedRectangle(background, badge, 8);
        graphics.DrawRoundedRectangle(border, badge, 8);
        graphics.DrawString(text, font, foreground, badge.X + 9, badge.Y + 4);
    }

    private static void DrawMetricCard(
        Graphics graphics,
        RectangleF bounds,
        double median,
        double p95,
        double cv,
        double speedup,
        Font metricFont,
        Font bodyFont,
        Brush primary,
        Brush muted,
        Color accent)
    {
        using SolidBrush card = new(Color.FromArgb(16, 31, 55));
        using Pen border = new(Color.FromArgb(105, accent), 1);
        using SolidBrush speedBrush = new(accent);
        graphics.FillRoundedRectangle(card, bounds, 11);
        graphics.DrawRoundedRectangle(border, bounds, 11);
        graphics.DrawString($"{median:0.0000} ms", metricFont, primary, bounds.X + 16, bounds.Y + 10);
        graphics.DrawString($"{speedup:0.000}×", metricFont, speedBrush, bounds.Right - 130, bounds.Y + 10);
        graphics.DrawString($"p95 {p95:0.0000} ms   ·   CV {cv:P2}", bodyFont, muted, bounds.X + 18, bounds.Y + 57);
    }

    private static void DrawCadenceStrip(
        Graphics graphics,
        RectangleF bounds,
        FrameCadenceSnapshot cadence,
        int submitted,
        Font font,
        Color accent)
    {
        using SolidBrush card = new(Color.FromArgb(12, 25, 45));
        using SolidBrush text = new(Color.FromArgb(226, 235, 247));
        using SolidBrush accentBrush = new(accent);
        using SolidBrush railBrush = new(Color.FromArgb(42, 59, 82));
        using Pen border = new(Color.FromArgb(90, accent), 1);
        graphics.FillRoundedRectangle(card, bounds, 9);
        graphics.DrawRoundedRectangle(border, bounds, 9);
        graphics.DrawString(
            $"done {cadence.Completed}/{submitted}   backlog {cadence.Backlog}   missed {cadence.MissedDeadlines}   remaining {cadence.OutstandingWorkMilliseconds:0.0} ms",
            font,
            text,
            bounds.X + 12,
            bounds.Y + 7);
        float progress = submitted == 0 ? 0 : cadence.Completed / (float)submitted;
        RectangleF rail = new(bounds.X + 12, bounds.Bottom - 12, bounds.Width - 24, 5);
        graphics.FillRectangle(railBrush, rail);
        graphics.FillRectangle(accentBrush, rail.X, rail.Y, rail.Width * progress, rail.Height);
    }

    private static void EncodeGif(string framesDirectory, string outputPath)
    {
        string pattern = Path.Combine(framesDirectory, "frame-%03d.png");
        string filter = "tpad=stop_mode=clone:stop_duration=1,scale=960:-2:flags=lanczos," +
            "split[s0][s1];[s0]palettegen=max_colors=96:stats_mode=diff[p];" +
            "[s1][p]paletteuse=dither=bayer:bayer_scale=3";
        RunFfmpeg([
            "-hide_banner", "-loglevel", "error", "-y",
            "-framerate", FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-i", pattern,
            "-vf", filter,
            "-loop", "0",
            outputPath
        ]);
    }

    private static void EncodeMp4(string framesDirectory, string outputPath)
    {
        string pattern = Path.Combine(framesDirectory, "frame-%03d.png");
        RunFfmpeg([
            "-hide_banner", "-loglevel", "error", "-y",
            "-framerate", FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-i", pattern,
            "-c:v", "libx264", "-preset", "medium", "-crf", "18",
            "-pix_fmt", "yuv420p", "-movflags", "+faststart",
            outputPath
        ]);
    }

    private static void RunFfmpeg(IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffmpeg.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed with exit code {process.ExitCode}: {output}");
    }

    private sealed class FrameAtlas : IDisposable
    {
        private readonly Bitmap[] frames;

        public FrameAtlas(ReadOnlyMemory<byte> rgba, int width, int height, int frameCount)
        {
            int frameBytes = checked(width * height * 4);
            if (rgba.Length != checked(frameBytes * frameCount))
                throw new InvalidDataException("GPU Crowd/VFX atlas length does not match its scene dimensions.");
            frames = new Bitmap[frameCount];
            for (int frame = 0; frame < frameCount; ++frame)
                frames[frame] = CreateBitmap(rgba.Span.Slice(frame * frameBytes, frameBytes), width, height);
        }

        public Bitmap this[int index] => frames[index];

        public void Dispose()
        {
            foreach (Bitmap frame in frames)
                frame.Dispose();
        }

        private static Bitmap CreateBitmap(ReadOnlySpan<byte> rgba, int width, int height)
        {
            Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[width * 4];
                for (int y = 0; y < height; ++y)
                {
                    ReadOnlySpan<byte> source = rgba.Slice(y * width * 4, width * 4);
                    for (int x = 0; x < width; ++x)
                    {
                        row[x * 4] = source[x * 4 + 2];
                        row[x * 4 + 1] = source[x * 4 + 1];
                        row[x * 4 + 2] = source[x * 4];
                        row[x * 4 + 3] = source[x * 4 + 3];
                    }
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }
    }

    private static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using GraphicsPath path = RoundedRectangle(bounds, radius);
        graphics.FillPath(brush, path);
    }

    private static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF bounds, float radius)
    {
        using GraphicsPath path = RoundedRectangle(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2;
        GraphicsPath path = new();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed record CrowdVisualArtifacts(string GifPath, string Mp4Path, string FramesDirectory);
