using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Windows;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.Platform.Windows.Capture;

public sealed record DiagnosticProgress(
    int CapturedFrames,
    int SavedFrames,
    string SessionDirectory,
    BitmapSource? Preview,
    BitmapSource? AnalysisFrame = null,
    GwentVisualObservation? Observation = null,
    string? Error = null,
    DateTimeOffset? SampledAt = null,
    double PreviewChange = 0, bool PointerInPlayerHand = false, double OpponentPreviewChange = 0,
    double TrackingPriority = 0, bool ResultsOnly = false);

public sealed class DiagnosticCaptureSession : IAsyncDisposable
{
    private readonly GwentWindowService _locator = new();
    private readonly Func<nint, GwentWindowSnapshot> _snapshot;
    private readonly Func<GwentWindowSnapshot, BitmapSource> _captureFrame;
    private readonly Func<int, GwentWindowSnapshot?>? _rediscoverWindow;
    private readonly string _sessionRoot;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private volatile bool _retainTrainingFrames;
    private volatile int _retainedFramesPerSecond = TrainingRecordingFrameRate.Recommended;
    private long? _expectedGameProcessStartTicks;

    public DiagnosticCaptureSession(
        Win32FrameCapture? capture,
        string? sessionRoot = null,
        Func<nint, GwentWindowSnapshot>? snapshot = null,
        Func<GwentWindowSnapshot, BitmapSource>? captureFrame = null,
        Func<int, GwentWindowSnapshot?>? rediscoverWindow = null)
    {
        if (capture is null && captureFrame is null)
            throw new ArgumentNullException(nameof(capture), "Provide a frame capture source or a capture delegate.");
        _snapshot = snapshot ?? _locator.Snapshot;
        _captureFrame = captureFrame ?? capture!.Capture;
        _rediscoverWindow = rediscoverWindow;
        _sessionRoot = sessionRoot ?? ResolveSessionRoot();
    }

    public event EventHandler<DiagnosticProgress>? Progress;

    public bool IsRunning => _worker is { IsCompleted: false };
    public bool RetainTrainingFrames { get => _retainTrainingFrames; set => _retainTrainingFrames = value; }
    public int RetainedFramesPerSecond
    {
        get => _retainedFramesPerSecond;
        set => _retainedFramesPerSecond = TrainingRecordingFrameRate.Normalize(value);
    }
    public string? CurrentSessionDirectory { get; private set; }

    public void Start(GwentWindowSnapshot window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (IsRunning)
        {
            throw new InvalidOperationException("A diagnostic capture session is already running.");
        }

        CurrentSessionDirectory = Path.Combine(
            _sessionRoot,
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(CurrentSessionDirectory);

        _expectedGameProcessStartTicks = TryGetGwentProcessStartTicks(window.ProcessId);
        _cancellation = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(window, CurrentSessionDirectory, _cancellation.Token));
    }

    public async Task StopAsync()
    {
        if (_worker is null)
        {
            return;
        }

        _cancellation?.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_cancellation?.IsCancellationRequested != true) throw;
            // Expected when the user stops a session.
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _worker = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(GwentWindowSnapshot initialWindow, string directory, CancellationToken cancellationToken)
    {
        const int intervalMilliseconds = 100;

        var window = initialWindow;
        var started = DateTimeOffset.UtcNow;
        var captured = 0;
        var saved = 0;
        var recordingFramesDropped = 0;
        var reviewFramesDropped = 0;
        long savedBytes = 0;
        var captureInterruptions = 0;
        var hasWaitedForRecovery = false;
        var lastSavedAt = DateTimeOffset.MinValue;
        var visualDetector = new GwentVisualStateDetector();
        var motionSampler = new PreviewMotionSampler();
        ReviewKeyFrameWriter? review = null;
        var lastReviewAt = DateTimeOffset.MinValue;
        string? reviewError = null;
        string? recordingError = null;
        string? captureError = null;
        // Disk/JPEG work must never throttle the live capture/analysis loop.
        // Two frozen 1280-wide frames bound queued memory to roughly 7 MiB.
        var recordingQueue = Channel.CreateBounded<(BitmapSource Frame, string Path)>(new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var recordingWriter = Task.Run(async () =>
        {
            await foreach (var pending in recordingQueue.Reader.ReadAllAsync())
            {
                try
                {
                    SaveJpeg(pending.Frame, pending.Path);
                    Interlocked.Add(ref savedBytes, new FileInfo(pending.Path).Length);
                    Interlocked.Increment(ref saved);
                    Volatile.Write(ref recordingError, null);
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref recordingError, "Training recording writer: " + exception.Message);
                    try { if (File.Exists(pending.Path)) File.Delete(pending.Path); } catch { }
                }
            }
        });
        var reviewQueue = Channel.CreateBounded<(BitmapSource Frame, DateTimeOffset At, PreviewMotion Motion)>(
            new BoundedChannelOptions(2)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        var reviewWriter = Task.Run(async () =>
        {
            await foreach (var pending in reviewQueue.Reader.ReadAllAsync())
            {
                try
                {
                    (review ??= new ReviewKeyFrameWriter(Path.Combine(directory, "review-keyframes"))).Observe(pending.Frame,pending.At,pending.Motion);
                    Volatile.Write(ref reviewError, null);
                }
                catch (Exception exception) { Volatile.Write(ref reviewError, "Review pack: " + exception.Message); }
            }
            try { review?.Finish(); }
            catch (Exception exception) { Interlocked.CompareExchange(ref reviewError, "Review pack: " + exception.Message, null); }
        });

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var iterationFailed = false;
                try
                {
                    try
                    {
                        var refreshedWindow = _snapshot(window.Handle);
                        if (window.ProcessId > 0 && refreshedWindow.ProcessId > 0 && refreshedWindow.ProcessId != window.ProcessId)
                            throw new InvalidOperationException("GWENT window handle changed to a different process.");
                        window = refreshedWindow with
                        {
                            ProcessId = refreshedWindow.ProcessId > 0 ? refreshedWindow.ProcessId : window.ProcessId,
                        };
                    }
                    catch (Exception snapshotException) when (snapshotException is not OperationCanceledException)
                    {
                        var rediscovered = TryRediscover(window.ProcessId);
                        if (rediscovered is null)
                            throw new InvalidOperationException("GWENT window is temporarily unavailable; waiting to reconnect.", snapshotException);
                        window = rediscovered;
                    }

                    var fullFrame = _captureFrame(window);
                    var frame = ScaleToWidth(fullFrame, 1280);
                    // Keep the small opponent-board artwork at recording resolution.
                    // Preview-only passes downscale inside the pipeline; the queue is bounded.
                    var analysisFrame = frame;
                    var analysisPixels = BitmapFrameAdapter.ToPixelFrame(analysisFrame);
                    var observation = visualDetector.Analyze(analysisPixels);
                    Volatile.Write(ref captureError, null);
                    var previewMotion = new PreviewMotion(0, 0);
                    captured++;

                    var now = DateTimeOffset.UtcNow;
                    if (now - lastReviewAt >= TimeSpan.FromMilliseconds(190))
                    {
                        // Measure at the retained cadence, so a change on an intervening
                        // capture cannot disappear before it reaches the rolling buffer.
                        previewMotion = motionSampler.Measure(analysisPixels);
                        if (RetainTrainingFrames)
                        {
                            if(!reviewQueue.Writer.TryWrite((analysisFrame,now,previewMotion)))
                                Interlocked.Increment(ref reviewFramesDropped);
                        }
                        lastReviewAt = now;
                    }
                    // Periodic training footage is independent of the live detector's capture
                    // cadence. Contributors can retain 10 FPS for brief animations, while the
                    // balanced and low-storage settings substantially reduce disk use.
                    var periodic = now - lastSavedAt >= TrainingRecordingFrameRate.Interval(RetainedFramesPerSecond);
                    if (RetainTrainingFrames && (lastSavedAt == DateTimeOffset.MinValue || periodic))
                    {
                        // Keep the established local-time filename convention for
                        // human browsing; timestamp values in stored data are UTC.
                        var file = Path.Combine(directory, $"frame-{captured:000000}-{now.ToLocalTime():HHmmssfff}.jpg");
                        if (recordingQueue.Writer.TryWrite((frame, file)))
                        {
                            lastSavedAt = now;
                        }
                        else Interlocked.Increment(ref recordingFramesDropped);
                    }

                    var preview = observation.View == GwentViewKind.MoveHistory || captured % 10 == 0
                        ? frame
                        : null;
                    PublishProgress(new DiagnosticProgress(
                        captured,
                        Volatile.Read(ref saved),
                        directory,
                        preview,
                        analysisFrame,
                        observation,
                        Error: Volatile.Read(ref captureError) ?? Volatile.Read(ref recordingError) ?? Volatile.Read(ref reviewError) ?? (Volatile.Read(ref recordingFramesDropped)>0 || Volatile.Read(ref reviewFramesDropped)>0
                                ? $"Storage is behind; {Volatile.Read(ref recordingFramesDropped)} recording and {Volatile.Read(ref reviewFramesDropped)} review samples skipped. Live analysis is unaffected."
                                : null),
                        SampledAt: now,
                        PreviewChange: previewMotion.Maximum, OpponentPreviewChange: previewMotion.Opponent));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    iterationFailed = true;
                    captureInterruptions++;
                    hasWaitedForRecovery = true;
                    Volatile.Write(ref captureError, exception.Message);
                    PublishProgress(new DiagnosticProgress(
                        captured,
                        saved,
                        directory,
                        null,
                        null,
                        Error: exception.Message));
                }

                try
                {
                    await Task.Delay(iterationFailed ? 1000 : intervalMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    captureInterruptions++;
                    hasWaitedForRecovery = true;
                    Volatile.Write(ref captureError, "Capture retry wait was interrupted; continuing. " + exception.Message);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected shutdown.
        }
        finally
        {
            recordingQueue.Writer.TryComplete();
            reviewQueue.Writer.TryComplete();
            await Task.WhenAll(recordingWriter,reviewWriter).ConfigureAwait(false);
            var manifest = new
            {
                Started = started,
                Ended = DateTimeOffset.UtcNow,
                CapturedFrames = captured,
                SavedFrames = Volatile.Read(ref saved),
                LastError = Volatile.Read(ref captureError),
                ReviewError = Volatile.Read(ref reviewError),
                RecordingError = Volatile.Read(ref recordingError),
                RecordingFramesDropped = Volatile.Read(ref recordingFramesDropped),
                ReviewFramesDropped = Volatile.Read(ref reviewFramesDropped),
                CaptureMethod = "Win32 BitBlt external window crop",
                SavedBytes = Interlocked.Read(ref savedBytes),
                TerminationReason = cancellationToken.IsCancellationRequested ? "UserStopped" : "UnexpectedWorkerExit",
                CaptureInterruptions = captureInterruptions,
                WaitedForRecovery = hasWaitedForRecovery,
                TrainingRecordingEnabledAtEnd = RetainTrainingFrames,
                RetainedFramesPerSecond,
                RetainedIntervalMilliseconds = 1000 / RetainedFramesPerSecond,
                MaximumImageWidth = 1280,
                Version = 5,
            };
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(directory, "session.json"), json).ConfigureAwait(false);
        }
    }

    private GwentWindowSnapshot? TryRediscover(int processId)
    {
        if (processId <= 0) return null;
        GwentWindowSnapshot? candidate;
        if (_rediscoverWindow is not null)
        {
            candidate = _rediscoverWindow(processId);
        }
        else
        {
            if (_expectedGameProcessStartTicks is not { } expectedStartTicks ||
                TryGetGwentProcessStartTicks(processId) != expectedStartTicks)
                return null;
            candidate = _locator.Find();
        }

        // Even injected rediscovery implementations cannot redirect a session to a
        // different process. This also prevents control-test captures from hopping
        // from the companion's own HWND into a real GWENT window.
        return candidate is { ProcessId: var candidateProcessId, Handle: not 0 } && candidateProcessId == processId
            ? candidate
            : null;
    }

    private static long? TryGetGwentProcessStartTicks(int processId)
    {
        if (processId <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.ProcessName.Equals("Gwent", StringComparison.OrdinalIgnoreCase)) return null;
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return null;
        }
    }

    private void PublishProgress(DiagnosticProgress progress)
    {
        if (Progress is not { } handlers) return;
        foreach (EventHandler<DiagnosticProgress> handler in handlers.GetInvocationList())
        {
            try { handler(this, progress); }
            catch { /* A UI observer must not terminate capture or other observers. */ }
        }
    }

    private static BitmapSource ScaleToWidth(BitmapSource source, int maximumWidth)
    {
        if (source.PixelWidth <= maximumWidth)
        {
            return source;
        }

        var scale = maximumWidth / (double)source.PixelWidth;
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        // Detach the small frame so the rolling buffer cannot retain full-resolution parents.
        var stride = (transformed.PixelWidth * transformed.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * transformed.PixelHeight];
        transformed.CopyPixels(pixels, stride, 0);
        var detached = BitmapSource.Create(transformed.PixelWidth, transformed.PixelHeight, 96, 96,
            transformed.Format, transformed.Palette, pixels, stride);
        detached.Freeze();
        return detached;
    }

    private static void SaveJpeg(BitmapSource source, string path)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 86 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string ResolveSessionRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            return Path.Combine(local, "GwentCompanion", "sessions");
        }

        return Path.Combine(AppContext.BaseDirectory, "sessions");
    }
}
