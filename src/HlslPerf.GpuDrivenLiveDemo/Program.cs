using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using HlslPerf.Core;
using HlslPerf.D3D12;
using HlslPerf.GpuDriven;

namespace HlslPerf.GpuDrivenLiveDemo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("The live Crowd/VFX demo requires Windows 10 or later.");

        LiveOptions options = LiveOptions.Parse(args);
        string repositoryRoot = FindRepositoryRoot();
        CrowdApplicationScene scene = new(
            options.AgentCount,
            options.Seed,
            options.VisibilityMask,
            options.Width,
            options.Height,
            Frames: 1);

        byte[] agents = CrowdApplication.GenerateAgents(scene.AgentCount, scene.Seed);
        UnifiedOperationPlan plan = CrowdApplication.Build(
            repositoryRoot,
            scene,
            agents,
            options.Arm,
            new string('0', 64));

        using D3D12Tuner tuner = new(options.Adapter);
        using D3D12Tuner.UnifiedExecutor executor = tuner.CreateUnifiedExecutor();
        using D3D12Tuner.UnifiedSession session = executor.Prepare(plan, options.MaximumAllocationBytes);

        ApplicationConfiguration.Initialize();
        using LiveCrowdForm form = new(scene, plan, session, options.Arm);
        Application.Run(form);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HlslKernelPipeline.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Run the live demo from inside the HlslKernelPipeline repository.");
    }
}

internal sealed class LiveCrowdForm : Form
{
    private readonly CrowdApplicationScene scene;
    private readonly UnifiedOperationPlan plan;
    private readonly D3D12Tuner.UnifiedSession session;
    private readonly string arm;
    private readonly System.Windows.Forms.Timer timer;
    private readonly Queue<double> frameTimes = new();
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private Bitmap? frame;
    private UnifiedLiveFrameTiming? timing;
    private long previousTicks;
    private uint animationFrame;
    private bool rendering;

    public LiveCrowdForm(
        CrowdApplicationScene scene,
        UnifiedOperationPlan plan,
        D3D12Tuner.UnifiedSession session,
        string arm)
    {
        this.scene = scene;
        this.plan = plan;
        this.session = session;
        this.arm = arm;

        Text = $"HLSL Kernel Pipeline · Live Crowd/VFX · {arm}";
        ClientSize = new Size(Math.Max(960, scene.Width * 2), Math.Max(600, scene.Height * 2));
        MinimumSize = new Size(720, 480);
        DoubleBuffered = true;
        BackColor = Color.FromArgb(12, 15, 22);

        timer = new System.Windows.Forms.Timer { Interval = 1 };
        timer.Tick += (_, _) => RenderNextFrame();
        Shown += (_, _) => timer.Start();
        FormClosed += (_, _) => timer.Stop();
    }

    private void RenderNextFrame()
    {
        if (rendering) return;
        rendering = true;
        try
        {
            uint frameIndex = animationFrame++ % 100_000u;
            IReadOnlyDictionary<string, uint[]> constants = CrowdApplication.FrameConstants(plan, frameIndex);
            byte[] rgba = session.ExecuteLiveFrame("frame-atlas", constants, out UnifiedLiveFrameTiming measured);
            timing = measured;

            Bitmap next = RgbaToBitmap(rgba, scene.Width, scene.Height);
            Bitmap? previous = frame;
            frame = next;
            previous?.Dispose();

            long now = wall.ElapsedTicks;
            if (previousTicks != 0)
            {
                double milliseconds = (now - previousTicks) * 1000.0 / Stopwatch.Frequency;
                frameTimes.Enqueue(milliseconds);
                while (frameTimes.Count > 120) frameTimes.Dequeue();
            }
            previousTicks = now;
            Invalidate();
        }
        finally
        {
            rendering = false;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        if (frame is null) return;

        Rectangle viewport = Fit(frame.Size, ClientRectangle, 24, 108);
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        e.Graphics.DrawImage(frame, viewport);

        using Font titleFont = new("Segoe UI", 16, FontStyle.Bold);
        using Font bodyFont = new("Consolas", 10, FontStyle.Regular);
        using Brush text = new SolidBrush(Color.White);
        using Brush muted = new SolidBrush(Color.FromArgb(190, 205, 215));
        using Brush panel = new SolidBrush(Color.FromArgb(205, 17, 22, 32));

        double meanFrameMs = frameTimes.Count == 0 ? 0 : frameTimes.Average();
        double fps = meanFrameMs > 0 ? 1000.0 / meanFrameMs : 0;
        UnifiedLiveFrameTiming? t = timing;
        string stats = t is null
            ? "warming up"
            : string.Create(CultureInfo.InvariantCulture,
                $"GPU render {t.GpuRenderMilliseconds,7:0.000} ms   " +
                $"GPU readback {t.GpuReadbackMilliseconds,6:0.000} ms   " +
                $"CPU fence {t.Submission.CpuFenceWaitMilliseconds,6:0.000} ms   " +
                $"present loop {meanFrameMs,6:0.00} ms / {fps,5:0.0} FPS");

        Rectangle overlay = new(16, 14, ClientSize.Width - 32, 78);
        e.Graphics.FillRectangle(panel, overlay);
        e.Graphics.DrawString("LIVE GPU CROWD/VFX", titleFont, text, 28, 20);
        e.Graphics.DrawString(
            $"{arm} · {scene.AgentCount:N0} agents · GPU visibility → scan/compact → tile binning → raster",
            bodyFont, muted, 30, 52);
        e.Graphics.DrawString(stats, bodyFont, text, 30, 70);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            frame?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Rectangle Fit(Size source, Rectangle target, int margin, int top)
    {
        int availableWidth = Math.Max(1, target.Width - margin * 2);
        int availableHeight = Math.Max(1, target.Height - top - margin);
        double scale = Math.Min((double)availableWidth / source.Width, (double)availableHeight / source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return new Rectangle((target.Width - width) / 2, top + (availableHeight - height) / 2, width, height);
    }

    private static Bitmap RgbaToBitmap(byte[] rgba, int width, int height)
    {
        int expected = checked(width * height * 4);
        if (rgba.Length != expected)
            throw new InvalidDataException($"Live atlas has {rgba.Length} bytes; expected {expected}.");

        byte[] bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }

        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData bits = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int sourceStride = width * 4;
            for (int y = 0; y < height; y++)
                Marshal.Copy(bgra, y * sourceStride, bits.Scan0 + y * bits.Stride, sourceStride);
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }
        return bitmap;
    }
}

internal sealed record LiveOptions(
    string Arm,
    int AgentCount,
    int Seed,
    uint VisibilityMask,
    int Width,
    int Height,
    string? Adapter,
    long MaximumAllocationBytes)
{
    public static LiveOptions Parse(string[] args)
    {
        string arm = "fused";
        int agents = 1_048_576;
        int seed = 69_501_203;
        uint mask = 511;
        int width = 480;
        int height = 270;
        string? adapter = null;
        long maximumAllocationBytes = 512L * 1024 * 1024;

        for (int i = 0; i < args.Length; i++)
        {
            string Value(string option)
            {
                if (++i >= args.Length) throw new ArgumentException(option + " requires a value.");
                return args[i];
            }

            switch (args[i])
            {
                case "--arm": arm = Value("--arm").ToLowerInvariant(); break;
                case "--agents": agents = int.Parse(Value("--agents"), CultureInfo.InvariantCulture); break;
                case "--seed": seed = int.Parse(Value("--seed"), CultureInfo.InvariantCulture); break;
                case "--visibility-mask": mask = uint.Parse(Value("--visibility-mask"), CultureInfo.InvariantCulture); break;
                case "--width": width = int.Parse(Value("--width"), CultureInfo.InvariantCulture); break;
                case "--height": height = int.Parse(Value("--height"), CultureInfo.InvariantCulture); break;
                case "--adapter": adapter = Value("--adapter"); break;
                case "--max-allocation-mib": maximumAllocationBytes = checked(long.Parse(Value("--max-allocation-mib"), CultureInfo.InvariantCulture) * 1024 * 1024); break;
                case "-h":
                case "--help":
                    MessageBox.Show(
                        "dotnet run --project src/HlslPerf.GpuDrivenLiveDemo -- [--arm fused|wave-tiled|hierarchical|rts] [--agents N] [--adapter name]\n\n" +
                        "The live path runs the real GPU Crowd/VFX operation every frame and synchronously reads its RGBA output for WinForms presentation. It does not use the offline GIF/MP4 composer.",
                        "Live Crowd/VFX demo");
                    Environment.Exit(0);
                    break;
                default: throw new ArgumentException("Unknown live-demo option '" + args[i] + "'.");
            }
        }

        if (!CrowdApplication.Arms.Contains(arm, StringComparer.Ordinal))
            throw new ArgumentException("--arm must be hierarchical, fused, wave-tiled, or rts.");
        if (agents is < 1 or > 16_777_216) throw new ArgumentOutOfRangeException("--agents");
        if (width is < 64 or > 1920 || height is < 64 or > 1080) throw new ArgumentOutOfRangeException("Live resolution");
        return new(arm, agents, seed, mask, width, height, adapter, maximumAllocationBytes);
    }
}
