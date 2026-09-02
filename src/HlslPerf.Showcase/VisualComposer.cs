using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using HlslPerf.Core;

namespace HlslPerf.Showcase;

internal sealed record VisualArtifacts(string GifPath, string Mp4Path, string FramesDirectory);

internal static class VisualComposer
{
    private const int CanvasWidth = 1200;
    private const int CanvasHeight = 676;
    private const int OutputFrameCount = 48;
    private const int FramesPerSecond = 12;

    public static VisualArtifacts Write(
        string level,
        DeviceFingerprint device,
        int elementCount,
        int scanRepeats,
        int sourceWidth,
        int sourceHeight,
        int sourceFrameCount,
        CandidateResult baseline,
        CandidateResult selected,
        ReadOnlyMemory<byte> baselineAtlas,
        ReadOnlyMemory<byte> selectedAtlas,
        string outputDirectory)
    {
        if (!baselineAtlas.Span.SequenceEqual(selectedAtlas.Span))
            throw new InvalidDataException("Baseline and tuned GPU frame atlases are not byte-identical.");
        if (baseline.Timing is null || selected.Timing is null)
            throw new InvalidDataException("Visual composition requires valid baseline and selected timings.");

        Directory.CreateDirectory(outputDirectory);
        string framesDirectory = Path.Combine(outputDirectory, "frames");
        Directory.CreateDirectory(framesDirectory);
        double speedup = baseline.Timing.MedianMilliseconds / selected.Timing.MedianMilliseconds;

        using FrameAtlas baselineFrames = new(baselineAtlas, sourceWidth, sourceHeight, sourceFrameCount);
        using FrameAtlas selectedFrames = new(selectedAtlas, sourceWidth, sourceHeight, sourceFrameCount);
        using Font titleFont = new("Segoe UI", 25, FontStyle.Bold, GraphicsUnit.Pixel);
        using Font subtitleFont = new("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel);
        using Font sectionFont = new("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
        using Font metricFont = new("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
        using Font bodyFont = new("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
        using Font monoFont = new("Consolas", 13, FontStyle.Regular, GraphicsUnit.Pixel);

        for (int outputFrame = 0; outputFrame < OutputFrameCount; ++outputFrame)
        {
            int tunedFrame = (int)Math.Floor(outputFrame * 0.58) % sourceFrameCount;
            int baselineFrame = (int)Math.Floor(outputFrame * 0.58 / Math.Max(1, speedup)) % sourceFrameCount;
            using Bitmap canvas = new(CanvasWidth, CanvasHeight, PixelFormat.Format32bppArgb);
            using Graphics graphics = Graphics.FromImage(canvas);
            Configure(graphics);
            DrawBackground(graphics);

            using SolidBrush primary = new(Color.FromArgb(239, 246, 255));
            using SolidBrush muted = new(Color.FromArgb(148, 163, 184));
            using SolidBrush cyan = new(Color.FromArgb(65, 210, 255));
            using SolidBrush green = new(Color.FromArgb(63, 231, 164));
            graphics.DrawString("SCAN PARTICLE FIELD · ACTUAL GPU OUTPUT", titleFont, primary, 38, 26);
            graphics.DrawString(
                $"{device.AdapterName} · D3D12 · {level.ToUpperInvariant()} pressure · " +
                $"{elementCount:N0} flags × {scanRepeats} scan{(scanRepeats == 1 ? string.Empty : "s")}/plan",
                subtitleFont,
                muted,
                40,
                65);

            RectangleF leftPanel = new(38, 112, 548, 309);
            RectangleF rightPanel = new(614, 112, 548, 309);
            DrawPanel(graphics, baselineFrames[baselineFrame], leftPanel, Color.FromArgb(63, 145, 255));
            DrawPanel(graphics, selectedFrames[tunedFrame], rightPanel, Color.FromArgb(63, 231, 164));

            graphics.DrawString("BASELINE", sectionFont, cyan, 44, 88);
            graphics.DrawString("TUNED", sectionFont, green, 620, 88);
            graphics.DrawString(ShortCandidate(baseline), monoFont, primary, 42, 431);
            graphics.DrawString(ShortCandidate(selected), monoFont, primary, 618, 431);

            DrawMetricCard(
                graphics,
                new RectangleF(38, 466, 548, 104),
                baseline.Timing.MedianMilliseconds,
                baseline.Timing.P95Milliseconds,
                baseline.Timing.CoefficientOfVariation,
                1,
                metricFont,
                bodyFont,
                primary,
                muted,
                Color.FromArgb(63, 145, 255));
            DrawMetricCard(
                graphics,
                new RectangleF(614, 466, 548, 104),
                selected.Timing.MedianMilliseconds,
                selected.Timing.P95Milliseconds,
                selected.Timing.CoefficientOfVariation,
                speedup,
                metricFont,
                bodyFont,
                primary,
                muted,
                Color.FromArgb(63, 231, 164));

            string equalityHash = selected.Correctness?.ActualSha256[..12] ?? "unavailable";
            graphics.DrawString(
                $"GPU buffers byte-identical · SHA-256 {equalityHash}… · playback pace follows measured median GPU time",
                bodyFont,
                green,
                40,
                594);
            graphics.DrawString(
                "Sequential captures · same seed / frame atlas / resolution / work · upload, readback and CPU composition excluded",
                bodyFont,
                muted,
                40,
                621);

            string framePath = Path.Combine(framesDirectory, $"frame-{outputFrame:D3}.png");
            canvas.Save(framePath, ImageFormat.Png);
        }

        string stem = $"scan-particles-{level}-ab";
        string gifPath = Path.Combine(outputDirectory, stem + ".gif");
        string mp4Path = Path.Combine(outputDirectory, stem + ".mp4");
        EncodeGif(framesDirectory, gifPath);
        EncodeMp4(framesDirectory, mp4Path);
        return new VisualArtifacts(gifPath, mp4Path, framesDirectory);
    }

    private static void Configure(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    private static void DrawBackground(Graphics graphics)
    {
        using LinearGradientBrush gradient = new(
            new Rectangle(0, 0, CanvasWidth, CanvasHeight),
            Color.FromArgb(6, 15, 30),
            Color.FromArgb(12, 23, 43),
            LinearGradientMode.ForwardDiagonal);
        graphics.FillRectangle(gradient, 0, 0, CanvasWidth, CanvasHeight);
    }

    private static void DrawPanel(Graphics graphics, Bitmap source, RectangleF destination, Color accent)
    {
        using SolidBrush card = new(Color.FromArgb(13, 27, 49));
        using Pen border = new(Color.FromArgb(95, accent), 2);
        graphics.FillRoundedRectangle(card, destination, 14);
        RectangleF imageDestination = new(destination.X + 8, destination.Y + 8, destination.Width - 16, destination.Height - 16);
        graphics.DrawImage(source, imageDestination);
        graphics.DrawRoundedRectangle(border, destination, 14);
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
        graphics.FillRoundedRectangle(card, bounds, 12);
        graphics.DrawRoundedRectangle(border, bounds, 12);
        graphics.DrawString($"{median:0.0000} ms", metricFont, primary, bounds.X + 16, bounds.Y + 12);
        graphics.DrawString($"{speedup:0.000}× baseline", metricFont, speedBrush, bounds.X + 282, bounds.Y + 12);
        graphics.DrawString($"p95 {p95:0.0000} ms   ·   CV {cv:P2}", bodyFont, muted, bounds.X + 18, bounds.Y + 66);
    }

    private static string ShortCandidate(CandidateResult candidate)
    {
        int group = candidate.Defines["HLSLPERF_GROUP_SIZE"];
        int elementsPerThread = candidate.Defines["HLSLPERF_ELEMENTS_PER_THREAD"];
        string backend = candidate.Defines.TryGetValue("HLSLPERF_SCAN_BACKEND", out int value) && value == 2
            ? "wave"
            : "blelloch";
        return $"{backend} · group={group} · EPT={elementsPerThread}";
    }

    private static void EncodeGif(string framesDirectory, string outputPath)
    {
        string pattern = Path.Combine(framesDirectory, "frame-%03d.png");
        string filter = "tpad=stop_mode=clone:stop_duration=1,scale=960:-2:flags=lanczos," +
            "split[s0][s1];[s0]palettegen=max_colors=64:stats_mode=diff[p];" +
            "[s1][p]paletteuse=dither=bayer:bayer_scale=4";
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
            "-c:v", "libx264", "-preset", "medium", "-crf", "20",
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
                throw new InvalidDataException("GPU frame atlas length does not match its dimensions.");
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
