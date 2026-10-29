#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.ScreenRecordingLib.Audio;
using ShareX.ScreenRecordingLib.Encoding;
using ShareX.ScreenRecordingLib.Native;
using ShareX.ScreenRecordingLib.Video;
using System.Drawing;
using Vortice.MediaFoundation;

namespace ShareX.ScreenRecordingLib;

/// <summary>A single native recording session. Prepare during a countdown to remove setup from StartAsync.</summary>
public sealed class ScreenRecorder : IDisposable, IAsyncDisposable
{
    private readonly RecordingOptions options;
    private readonly RecordingClock clock = new();
    private readonly object sync = new();
    private readonly ManualResetEvent stop = new(false);
    private readonly ManualResetEvent begin = new(false);
    private readonly AutoResetEvent changed = new(false);
    private readonly TaskCompletionSource prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RecordingResult> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? worker;
    private bool startRequested, disposed, resumeRequested;
    private Rectangle? resumeRegion;
    private int recording, aborted;

    public Task<RecordingResult> Completion => completed.Task;
    public VideoEncoderInfo? Encoder { get; private set; }
    public CameraCaptureInfo? Camera { get; private set; }
    /// <summary>Nonfatal camera errors; screen and audio recording continue.</summary>
    public event Action<string>? Diagnostic;
    /// <summary>GIF encoding progress after capture has stopped, from 0 to 100.</summary>
    public event Action<int>? EncodingProgressChanged;
    public bool IsRecording => Volatile.Read(ref recording) != 0;
    public bool IsPaused => clock.IsPaused;
    public TimeSpan Elapsed => TimeSpan.FromTicks(clock.Elapsed);

    public ScreenRecorder(RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options with { OutputPath = Path.GetFullPath(options.OutputPath) };
    }

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (worker == null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                worker = new Thread(Run) { IsBackground = true, Name = "ShareX native recording" };
                worker.Start();
            }
        }
        // Cancellation stops this session, including its capture threads and partially created output.
        using CancellationTokenRegistration registration = cancellationToken.Register(RequestStop);
        await prepared.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await PrepareAsync(cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (startRequested) throw new InvalidOperationException("A recorder instance can only be started once.");
            startRequested = true;
            begin.Set();
        }
        using CancellationTokenRegistration registration = cancellationToken.Register(RequestStop);
        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Pause()
    {
        lock (sync)
        {
            if (disposed) return;
            resumeRequested = false;
            resumeRegion = null;
            clock.Pause();
            changed.Set();
        }
    }

    public void Resume() => RequestResume(null);

    /// <summary>Resume desktop capture at a new position, keeping the recording's output dimensions.</summary>
    public void Resume(Rectangle region)
    {
        if (options.WindowHandle != 0) throw new InvalidOperationException("Window capture follows its target window rather than a desktop region.");
        int sizeMask = options.IsGif ? -1 : ~1;
        if ((region.Width & sizeMask) != (options.Region.Width & sizeMask) || (region.Height & sizeMask) != (options.Region.Height & sizeMask))
            throw new ArgumentException("Moving the recording region cannot change its output dimensions.", nameof(region));
        RequestResume(region);
    }

    private void RequestResume(Rectangle? region)
    {
        lock (sync)
        {
            if (disposed || !clock.IsPaused) return;
            resumeRegion = region;
            resumeRequested = true;
            changed.Set();
        }
    }

    /// <summary>Nonblocking stop request, safe from a UI thread. Await StopAsync or Completion to finalize the output.</summary>
    public void RequestStop()
    {
        lock (sync) { if (!disposed) stop.Set(); }
    }

    /// <summary>Discard this recording and cancel any pending GIF encoding.</summary>
    public void RequestAbort()
    {
        Interlocked.Exchange(ref aborted, 1);
        RequestStop();
    }

    public Task<RecordingResult> StopAsync()
    {
        lock (sync)
        {
            if (worker == null) throw new InvalidOperationException("The recorder has not been prepared.");
            if (!disposed) stop.Set();
        }
        return Completion;
    }

    private unsafe void Run()
    {
        bool com = false, mediaFoundation = false, ownsOutput = false;
        List<string> ownedCompanions = [];
        try
        {
            NativeMethods.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED).ThrowOnFailure();
            com = true;
            if (!options.IsGif || options.CaptureCamera)
            {
                MediaFactory.MFStartup(true).CheckError();
                mediaFoundation = true;
            }
            List<GraphicsCapture.Target> targets = GraphicsCapture.GetTargets(options, out int width, out int height);
            Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
            // Reserve the name atomically. Never overwrite or delete a pre-existing user file.
            using (new FileStream(options.OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            ownsOutput = true;
            // Lesson mode: reserve the companion track files the same way, beside the screen video.
            bool separate = options.SeparateTracks;
            string? cameraPath = separate && options.CaptureCamera ? LessonFiles.TrackPath(options.OutputPath, LessonTrackKind.Camera) : null;
            string? voicePath = separate && options.CaptureMicrophone && !options.CaptureCamera ? LessonFiles.TrackPath(options.OutputPath, LessonTrackKind.Voice) : null;
            string? systemPath = separate && options.CaptureSystemAudio ? LessonFiles.TrackPath(options.OutputPath, LessonTrackKind.SystemAudio) : null;
            foreach (string? companion in new[] { cameraPath, voicePath, systemPath })
            {
                if (companion == null) continue;
                using (new FileStream(companion, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                ownedCompanions.Add(companion);
            }
            using GraphicsDevice graphics = new(targets[0].Monitor);
            using GpuVideoProcessor processor = new(graphics, width, height, options.FramesPerSecond, !options.IsGif);
            // In lesson mode the screen file is video only; audio goes to the companion tracks.
            using MediaFoundationWriter? writer = options.IsGif ? null : new(graphics, options.OutputPath, width, height, options.FramesPerSecond,
                options.VideoBitrate, options.RequireHardwareEncoder, !separate && options.HasAudio ? options.AudioBitrate : 0);
            using GifWriter? gifWriter = options.IsGif ? new(graphics, options, width, height) : null;
            Encoder = writer?.Encoder ?? GifWriter.Encoder;
            using VideoTexturePool? textures = writer == null ? null : new(graphics, processor, writer.EncoderBindFlags);
            using GraphicsCapture capture = new(graphics, processor, targets, options.IncludeCursor, options.FramesPerSecond);
            using CameraCapture? camera = options.CaptureCamera ? new(graphics, options) : null;
            Camera = camera?.Info;
            using CameraOverlay? cameraOverlay = camera == null || separate ? null : new(graphics, camera.Info, camera.Subtype,
                camera.Stride, camera.YuvMatrix, camera.NominalRange, options, width, height);
            // Lesson mode camera track: the whole camera frame, encoded at the camera's size and rate.
            using CameraOverlay? trackOverlay = camera == null || !separate ? null : new(graphics, camera.Info, camera.Subtype,
                camera.Stride, camera.YuvMatrix, camera.NominalRange, options, camera.Info.Width, camera.Info.Height, fullFrame: true);
            int cameraFps = options.CameraFramesPerSecond;
            using GpuVideoProcessor? cameraProcessor = trackOverlay == null ? null : new(graphics, camera!.Info.Width, camera.Info.Height, cameraFps, true);
            cameraProcessor?.Clear(); // Black until the camera's first frame arrives.
            using MediaFoundationWriter? cameraWriter = cameraProcessor == null ? null : new(graphics, cameraPath!, camera!.Info.Width, camera.Info.Height,
                cameraFps, CameraBitrate(camera.Info), options.RequireHardwareEncoder, options.CaptureMicrophone ? options.AudioBitrate : 0);
            using VideoTexturePool? cameraTextures = cameraWriter == null ? null : new(graphics, cameraProcessor!, cameraWriter.EncoderBindFlags);
            using MediaFoundationWriter? voiceWriter = voicePath == null ? null : new(null, voicePath, 0, 0, 0, 0, false, options.AudioBitrate);
            using MediaFoundationWriter? systemWriter = systemPath == null ? null : new(null, systemPath, 0, 0, 0, 0, false, options.AudioBitrate);
            AudioMixer mixer = new(clock);
            List<AudioOutput> audioOutputs = [];
            if (!separate)
            {
                if (writer != null && options.HasAudio) audioOutputs.Add(new(writer, AudioMixer.AllSources));
            }
            else
            {
                if (systemWriter != null) audioOutputs.Add(new(systemWriter, 1 << AudioMixer.SystemSource));
                MediaFoundationWriter? microphoneWriter = options.CaptureMicrophone ? cameraWriter ?? voiceWriter : null;
                if (microphoneWriter != null) audioOutputs.Add(new(microphoneWriter, 1 << AudioMixer.MicrophoneSource));
            }
            using WasapiCapture? audio = options.HasAudio ? new(options, mixer) : null;
            prepared.TrySetResult();
            if (WaitHandle.WaitAny([stop, begin]) == 0) throw new OperationCanceledException("Recording stopped before capture began.");
            camera?.Start();
            audio?.Start();
            capture.Start();
            long waitStart = RecordingClock.Now;
            while (!capture.HasFrame)
            {
                if (stop.WaitOne(5)) throw new OperationCanceledException("Recording stopped before the first video frame.");
                if (audio?.Failure is Exception audioFailure) throw new IOException("Windows audio capture failed.", audioFailure);
                capture.Update();
                if (capture.HasEnded) throw new IOException("The capture target closed or changed size before recording started.");
                if (RecordingClock.Now - waitStart > TimeSpan.FromSeconds(10).Ticks)
                    throw new TimeoutException("Windows did not deliver a capture frame. The window may be minimized, protected, or unavailable.");
            }
            clock.Start(capture.LatestTimestamp);
            Volatile.Write(ref recording, 1);
            long nextFrame = 0, written = 0, dropped = 0;
            bool discontinuity = false;
            bool cameraDisabled = false;
            long nextCameraFrame = 0, lastCameraFrame = 0;
            long cameraTrackFrame = 0, cameraWritten = 0, cameraDropped = 0;
            bool cameraDiscontinuity = false;
            short[] audioBlock = new short[480 * 2]; // 10 ms, 48 kHz stereo
            using FrameTimer timer = new();
            WaitHandle[] waits = [stop, changed, timer];
            long duration = 0;
            Rectangle currentRegion = options.Region;
            long resumeWaitStart = 0;
            while (!stop.WaitOne(0))
            {
                if (capture.HasEnded) break;
                if (audio?.Failure is Exception audioFailure) throw new IOException("Windows audio capture failed (the endpoint may have changed or been disconnected).", audioFailure);
                if (clock.IsPaused)
                {
                    Rectangle? requestedRegion;
                    lock (sync) requestedRegion = resumeRequested ? resumeRegion : null;
                    if (requestedRegion is Rectangle region && region != currentRegion)
                    {
                        // Only the capture sessions change. Keep the GPU, encoder, camera, audio and
                        // media timeline alive, including when the region moves onto another display.
                        List<GraphicsCapture.Target> movedTargets = GraphicsCapture.GetTargets(options with { Region = region }, out _, out _);
                        capture.Retarget(movedTargets);
                        currentRegion = region;
                        resumeWaitStart = RecordingClock.Now;
                    }
                    // Drain WGC so resume never displays frames retained during the pause.
                    capture.Update();
                    if (capture.HasEnded) break;
                    if (!capture.HasFrame && RecordingClock.Now - resumeWaitStart > TimeSpan.FromSeconds(10).Ticks)
                        throw new TimeoutException("Windows did not deliver a frame for the moved recording region.");
                    WriteAudioUntil(audioOutputs, mixer, audioBlock, clock.Elapsed);
                    lock (sync)
                    {
                        // Keep media time paused until the new region has a frame. A later pause or
                        // resume request supersedes this one without briefly recording an old target.
                        if (resumeRequested && resumeRegion == requestedRegion && capture.HasFrame)
                        {
                            clock.Resume();
                            resumeRequested = false;
                            resumeRegion = null;
                        }
                    }
                    discontinuity = true;
                    cameraDiscontinuity = true;
                    if (!clock.IsPaused) continue;
                    timer.Arm(TimeSpan.FromMilliseconds(20).Ticks);
                    if (WaitHandle.WaitAny(waits) == 0) break;
                    continue;
                }
                long elapsed = clock.Elapsed;
                if (options.Duration > TimeSpan.Zero && elapsed >= options.Duration.Ticks) break;
                long frameTime = nextFrame * TimeSpan.TicksPerSecond / options.FramesPerSecond;
                if (elapsed >= frameTime)
                {
                    // Skip overdue frame slots. Never accelerate playback or queue a catch-up burst.
                    long dueFrame = elapsed * options.FramesPerSecond / TimeSpan.TicksPerSecond;
                    if (written > 0 && dueFrame > nextFrame)
                    {
                        dropped += dueFrame - nextFrame;
                        nextFrame = dueFrame;
                        discontinuity = true;
                    }
                    capture.Update();
                    long timestamp = nextFrame * TimeSpan.TicksPerSecond / options.FramesPerSecond;
                    if (capture.HasEnded) break;
                    long end = (nextFrame + 1) * TimeSpan.TicksPerSecond / options.FramesPerSecond;
                    VideoTexturePool.Slot? slot = null;
                    if (textures == null || textures.TryRent(out slot))
                    {
                        if (cameraOverlay != null && camera != null && !cameraDisabled)
                        {
                            try
                            {
                                if (camera.Failure is Exception cameraFailure) throw cameraFailure;
                                // The camera has its own cadence. At high screen FPS, reuse its existing tile.
                                if (elapsed >= nextCameraFrame)
                                {
                                    IMFSample? cameraFrame = camera.TakeLatestFrame();
                                    if (cameraFrame != null)
                                    {
                                        cameraOverlay!.Update(cameraFrame);
                                        lastCameraFrame = elapsed;
                                    }
                                    // Align to media-clock slots so a late update does not keep
                                    // pushing the camera deadline onto the following screen frame.
                                    long nextCameraSlot = elapsed * options.CameraFramesPerSecond / TimeSpan.TicksPerSecond + 1;
                                    nextCameraFrame = (nextCameraSlot * TimeSpan.TicksPerSecond + options.CameraFramesPerSecond - 1) / options.CameraFramesPerSecond;
                                }
                                if (elapsed - lastCameraFrame > TimeSpan.FromSeconds(10).Ticks)
                                    throw new TimeoutException("The camera did not deliver a new frame for ten seconds.");
                            }
                            catch (Exception ex)
                            {
                                cameraDisabled = true;
                                cameraOverlay!.Disable();
                                ReportDiagnostic($"Camera overlay disabled; screen and audio recording continue. {ex}");
                                camera.Dispose();
                            }
                        }
                        if (gifWriter != null)
                        {
                            if (!gifWriter.TryWriteVideo(processor.GetBgraFrame(cameraOverlay), timestamp))
                            {
                                dropped++;
                                nextFrame++;
                                continue;
                            }
                        }
                        else
                        {
                            try { processor.Convert(slot!.View, (uint)nextFrame, cameraOverlay); }
                            catch { textures!.Return(slot!); throw; }
                            using var sample = textures!.CreateSample(slot!);
                            writer!.WriteVideo(sample, timestamp, end - timestamp, discontinuity);
                        }
                        written++;
                        duration = end;
                        discontinuity = false;
                        started.TrySetResult();
                    }
                    else { dropped++; discontinuity = true; }
                    nextFrame++;
                }
                if (cameraWriter != null && elapsed >= cameraTrackFrame * TimeSpan.TicksPerSecond / cameraFps)
                {
                    // Lesson mode camera track, on its own cadence and the shared media clock.
                    long dueCameraFrame = elapsed * cameraFps / TimeSpan.TicksPerSecond;
                    if (cameraWritten > 0 && dueCameraFrame > cameraTrackFrame)
                    {
                        cameraDropped += dueCameraFrame - cameraTrackFrame;
                        cameraTrackFrame = dueCameraFrame;
                        cameraDiscontinuity = true;
                    }
                    if (camera != null && !cameraDisabled)
                    {
                        try
                        {
                            if (camera.Failure is Exception cameraFailure) throw cameraFailure;
                            IMFSample? cameraSample = camera.TakeLatestFrame();
                            if (cameraSample != null)
                            {
                                trackOverlay!.Update(cameraSample);
                                lastCameraFrame = elapsed;
                            }
                            if (elapsed - lastCameraFrame > TimeSpan.FromSeconds(10).Ticks)
                                throw new TimeoutException("The camera did not deliver a new frame for ten seconds.");
                        }
                        catch (Exception ex)
                        {
                            // Keep the track valid and in sync: continue with black frames and the microphone.
                            cameraDisabled = true;
                            trackOverlay!.Disable();
                            ReportDiagnostic($"Camera stopped; the camera track continues with black video and the microphone. {ex}");
                            camera.Dispose();
                        }
                    }
                    long cameraTimestamp = cameraTrackFrame * TimeSpan.TicksPerSecond / cameraFps;
                    long cameraEnd = (cameraTrackFrame + 1) * TimeSpan.TicksPerSecond / cameraFps;
                    if (cameraTextures!.TryRent(out VideoTexturePool.Slot? cameraSlot))
                    {
                        try { cameraProcessor!.Convert(cameraSlot!.View, (uint)cameraTrackFrame, trackOverlay); }
                        catch { cameraTextures.Return(cameraSlot!); throw; }
                        using var sample = cameraTextures.CreateSample(cameraSlot!);
                        cameraWriter.WriteVideo(sample, cameraTimestamp, cameraEnd - cameraTimestamp, cameraDiscontinuity);
                        cameraWritten++;
                        cameraDiscontinuity = false;
                    }
                    else { cameraDropped++; cameraDiscontinuity = true; }
                    cameraTrackFrame++;
                }
                // Allow 50 ms for WASAPI packets to arrive; timestamps remain aligned with video.
                WriteAudioUntil(audioOutputs, mixer, audioBlock, Math.Max(0, clock.Elapsed - TimeSpan.FromMilliseconds(50).Ticks));
                long remaining = nextFrame * TimeSpan.TicksPerSecond / options.FramesPerSecond - clock.Elapsed;
                if (cameraWriter != null)
                    remaining = Math.Min(remaining, cameraTrackFrame * TimeSpan.TicksPerSecond / cameraFps - clock.Elapsed);
                timer.Arm(remaining);
                if (WaitHandle.WaitAny(waits) == 0) break;
            }
            // Stop and drain capture before writing the final audio blocks and finalizing the container.
            audio?.Dispose();
            clock.Stop();
            Volatile.Write(ref recording, 0);
            duration = Math.Max(duration, options.Duration > TimeSpan.Zero ? Math.Min(clock.Elapsed, options.Duration.Ticks) : clock.Elapsed);
            if (Volatile.Read(ref aborted) != 0) throw new OperationCanceledException("Recording aborted.");
            WriteAudioUntil(audioOutputs, mixer, audioBlock, duration, true);
            if (written == 0) throw new IOException("No video frames were accepted by the encoder.");
            if (cameraWriter != null && cameraWritten == 0) throw new IOException("No camera-track frames were accepted by the encoder.");
            writer?.Finish();
            cameraWriter?.Finish();
            voiceWriter?.Finish();
            systemWriter?.Finish();
            if (cameraDropped > 0) ReportDiagnostic($"Camera track skipped {cameraDropped} frame slots.");
            if (gifWriter != null)
            {
                capture.Dispose();
                camera?.Dispose();
                gifWriter.Finish(duration, ReportEncodingProgress, () => Volatile.Read(ref aborted) != 0);
            }
            LessonSession? lesson = separate ? BuildLesson(width, height, duration, cameraPath, camera?.Info, voicePath, systemPath) : null;
            completed.TrySetResult(new(options.OutputPath, TimeSpan.FromTicks(duration), written, dropped, mixer.Discontinuities, Encoder!, lesson));
        }
        catch (Exception ex)
        {
            if (ownsOutput)
            {
                try { File.Delete(options.OutputPath); }
                catch (Exception cleanup) { ex = new AggregateException("Recording failed and its incomplete output could not be removed.", ex, cleanup); }
            }
            foreach (string companion in ownedCompanions)
            {
                try { File.Delete(companion); }
                catch (Exception cleanup) { ex = new AggregateException("Recording failed and an incomplete lesson track could not be removed.", ex, cleanup); }
            }
            prepared.TrySetException(ex); started.TrySetException(ex); completed.TrySetException(ex);
        }
        finally
        {
            clock.Stop();
            Volatile.Write(ref recording, 0);
            if (mediaFoundation) MediaFactory.MFShutdown();
            if (com) NativeMethods.CoUninitialize();
        }
    }

    private void ReportDiagnostic(string message)
    {
        try { Diagnostic?.Invoke(message); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private void ReportEncodingProgress(int progress)
    {
        try { EncodingProgressChanged?.Invoke(progress); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    /// <summary>One audio destination and the mixer sources it receives.</summary>
    private sealed class AudioOutput(MediaFoundationWriter writer, int sources)
    {
        public MediaFoundationWriter Writer { get; } = writer;
        public int Sources { get; } = sources;
        public long Frame;
    }

    private static void WriteAudioUntil(List<AudioOutput> outputs, AudioMixer mixer, short[] block, long until, bool partial = false)
    {
        long endFrame = (long)((Int128)until * AudioMixer.SampleRate / TimeSpan.TicksPerSecond);
        foreach (AudioOutput audio in outputs)
        {
            while (audio.Frame + block.Length / 2 <= endFrame || (partial && audio.Frame < endFrame))
            {
                int count = (int)Math.Min(block.Length / 2, endFrame - audio.Frame);
                Span<short> output = block.AsSpan(0, count * 2);
                mixer.Read(audio.Frame, output, audio.Sources);
                audio.Writer.WriteAudio(output, audio.Frame);
                audio.Frame += count;
            }
        }
    }

    private int CameraBitrate(CameraCaptureInfo camera) =>
        (int)Math.Clamp((long)camera.Width * camera.Height * options.CameraFramesPerSecond / 8, 1_000_000L, Math.Max(1_000_000L, options.VideoBitrate));

    private LessonSession BuildLesson(int width, int height, long duration, string? cameraPath, CameraCaptureInfo? camera,
        string? voicePath, string? systemPath)
    {
        List<LessonTrack> tracks = [new(LessonTrackKind.Screen, options.OutputPath, HasVideo: true, HasAudio: false, width, height)];
        if (cameraPath != null && camera != null)
            tracks.Add(new(LessonTrackKind.Camera, cameraPath, HasVideo: true, HasAudio: options.CaptureMicrophone, camera.Width, camera.Height));
        if (voicePath != null) tracks.Add(new(LessonTrackKind.Voice, voicePath, HasVideo: false, HasAudio: true));
        if (systemPath != null) tracks.Add(new(LessonTrackKind.SystemAudio, systemPath, HasVideo: false, HasAudio: true));
        return new()
        {
            DurationSeconds = TimeSpan.FromTicks(duration).TotalSeconds,
            ScreenWidth = width,
            ScreenHeight = height,
            Tracks = tracks,
            Face = new()
            {
                Position = options.CameraPosition,
                Shape = options.CameraShape,
                WidthPercent = options.CameraWidthPercent,
                Margin = options.CameraMargin
            }
        };
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            stop.Set();
        }
        worker?.Join();
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            stop.Dispose(); begin.Dispose(); changed.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        RequestStop();
        if (worker != null) { try { await Completion.ConfigureAwait(false); } catch { } }
        Dispose();
    }
}
