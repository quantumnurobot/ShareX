// Pakwone lesson mode: end-to-end tests for LessonEdit, LessonFiles and LessonExport.
// Generates synthetic screen / camera+voice / system-sound tracks with FFmpeg, then checks every export.
// Usage: dotnet run --project Pakwone.Tests/LessonExportTests -c Release -p:Platform=x64  (ffmpeg and ffprobe on PATH)
using ShareX.ScreenRecordingLib;
using System.Diagnostics;
using System.Globalization;

int failures = 0;
void Check(bool condition, string what)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {what}");
    if (!condition) failures++;
}
bool Near(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;

string Run(string file, params string[] args)
{
    ProcessStartInfo info = new(file) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string arg in args) info.ArgumentList.Add(arg);
    using Process process = Process.Start(info)!;
    Task<string> err = process.StandardError.ReadToEndAsync();
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new Exception($"{file} failed: {err.Result}");
    return output;
}
double Duration(string path) => double.Parse(Run("ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path).Trim(), CultureInfo.InvariantCulture);
string Streams(string path) => Run("ffprobe", "-v", "error", "-show_entries", "stream=codec_type,width,height,sample_rate", "-of", "csv=p=0", path).Trim().Replace("\n", " | ");

// ---------- Pure edit logic ----------
LessonEdit e = new() { TrimStart = 1, TrimEnd = 11, Cuts = [new(3, 5), new(4, 6), new(8, 8.02), new(20, 30)] };
var kept = e.KeptSegments(12);
Check(kept.Count == 3 && kept[0] == new LessonSegment(1, 3) && kept[1] == new LessonSegment(6, 8) && kept[2] == new LessonSegment(8.02, 11),
    $"segments: overlapping cuts merge, out-of-range cut ignored -> {string.Join(", ", kept)}");
Check(Near(e.EditedDuration(12), 2 + 2 + 2.98, 1e-9), "edited duration = 6.98 s");
Check(new LessonEdit { Cuts = [new(0, 12)] }.KeptSegments(12).Count == 0, "cutting everything leaves nothing");
Check(new LessonEdit { TrimStart = 5, TrimEnd = 2 }.Normalize(12) is { TrimStart: 5, TrimEnd: 5 }, "end before start clamps to start");
Check(new LessonEdit { TrimEnd = 12 }.Normalize(12).TrimEnd == null, "trim end at the end means 'to the end'");
Check(new LessonEdit { TrimStart = double.NaN, VoiceVolume = 99, FaceWidthPercent = 1 }.Normalize(12) is { TrimStart: 0, VoiceVolume: 4, FaceWidthPercent: 5 }, "invalid values are clamped");
Check(new LessonEdit { Cuts = [new(5, 3)] }.KeptSegments(10).Count == 2, "a reversed cut still cuts");

// ---------- Synthetic recorded tracks (what lesson mode writes) ----------
string dir = Path.Combine(Path.GetTempPath(), "pakwone-lesson-e2e");
if (Directory.Exists(dir)) Directory.Delete(dir, true);
Directory.CreateDirectory(dir);
string recordedScreen = Path.Combine(dir, "ShareX_2026-recording-abc.mp4");
string recordedCamera = LessonFiles.TrackPath(recordedScreen, LessonTrackKind.Camera);
string recordedSystem = LessonFiles.TrackPath(recordedScreen, LessonTrackKind.SystemAudio);
Check(recordedCamera.EndsWith("ShareX_2026-recording-abc.camera.mp4") && recordedSystem.EndsWith("ShareX_2026-recording-abc.system-audio.m4a"), "companion track names");
Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=12", "-c:v", "libx264", "-pix_fmt", "yuv420p", recordedScreen);
Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "smptebars=size=640x480:rate=30:duration=12", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=12",
    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2", recordedCamera);
Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=12", "-c:a", "aac", "-ac", "2", recordedSystem);

LessonSession recorded = new()
{
    DurationSeconds = 12, ScreenWidth = 1280, ScreenHeight = 720,
    Tracks = [new(LessonTrackKind.Screen, recordedScreen, true, false, 1280, 720),
              new(LessonTrackKind.Camera, recordedCamera, true, true, 640, 480),
              new(LessonTrackKind.SystemAudio, recordedSystem, false, true)],
    Face = new() { Position = CameraOverlayPosition.TopLeft, Shape = CameraOverlayShape.Circle, WidthPercent = 25, Margin = 20 }
};

// ---------- Commit: rename like ScreenRecordManager does, then commit companions ----------
string finalScreen = Path.Combine(dir, "My Lesson 1.mp4");
File.Move(recordedScreen, finalScreen);
LessonSession committed = LessonFiles.Commit(recorded, finalScreen, replaceExisting: false);
string manifest = LessonFiles.ManifestPath(finalScreen);
Check(File.Exists(Path.Combine(dir, "My Lesson 1.camera.mp4")) && File.Exists(Path.Combine(dir, "My Lesson 1.system-audio.m4a")) && !File.Exists(recordedCamera),
    "commit moves companions to the final name");
Check(File.Exists(manifest) && manifest.EndsWith("My Lesson 1.lesson.json"), "commit writes My Lesson 1.lesson.json");
LessonSession loaded = LessonFiles.Load(manifest);
Check(loaded.Tracks.All(t => !Path.IsPathRooted(t.File)) && loaded.Tracks.Count == 3 && loaded.Face.Shape == CameraOverlayShape.Circle, "manifest stores relative names and round-trips");
Check(loaded.VoiceSource?.Kind == LessonTrackKind.Camera, "voice comes from the camera track");
bool threw = false;
try { LessonFiles.Commit(recorded, finalScreen, replaceExisting: false); } catch (IOException) { threw = true; }
Check(threw, "commit refuses to overwrite an existing lesson unless asked");
Console.WriteLine("manifest:\n" + File.ReadAllText(manifest));

LessonEdit startEdit = LessonEdit.FromSession(loaded);
Check(startEdit is { FacePlacement: LessonFacePlacement.TopLeft, FaceShape: CameraOverlayShape.Circle, FaceWidthPercent: 25, FaceMargin: 20 }, "editor starts from the recorded face layout");

// Save edits into the manifest and reload.
LessonEdit edit = startEdit with { TrimStart = 1, TrimEnd = 11, Cuts = [new(3, 5)], SystemVolume = 0.5 };
LessonFiles.Save(loaded with { Edit = edit }, manifest);
Check(LessonFiles.Load(manifest).Edit == null ? false : LessonFiles.Load(manifest).Edit!.Cuts.Single() == new LessonCut(3, 5), "edits are saved in the lesson file");

// ---------- Exports through real FFmpeg ----------
string Export(LessonExportKind kind, LessonEdit with, string name, double? preview = null)
{
    string output = Path.Combine(dir, name);
    string[] args = LessonExport.BuildArguments(loaded, manifest, with, kind, output, preview);
    Run("ffmpeg", args);
    return output;
}

string blended = Export(LessonExportKind.Blended, edit, "blended.mp4");
Check(Near(Duration(blended), 8, 0.15), $"blended duration 8 s (trim 1-11 minus cut 3-5): {Duration(blended):0.000}");
Check(Streams(blended).Contains("video,1280,720") && Streams(blended).Contains("audio"), $"blended has 1280x720 video and audio: {Streams(blended)}");
Run("ffmpeg", "-v", "error", "-y", "-ss", "4", "-i", blended, "-frames:v", "1", Path.Combine(dir, "blended-frame.png"));

string custom = Export(LessonExportKind.Blended, edit with { FaceShape = CameraOverlayShape.Rectangle, FacePlacement = LessonFacePlacement.Custom, FaceX = 0.5, FaceY = 1, FaceWidthPercent = 30 }, "custom.mp4");
Run("ffmpeg", "-v", "error", "-y", "-ss", "2", "-i", custom, "-frames:v", "1", Path.Combine(dir, "custom-frame.png"));
Check(Near(Duration(custom), 8, 0.15), "custom-position rectangle export");

string noFace = Export(LessonExportKind.Blended, edit with { ShowFace = false, IncludeSystemAudio = false }, "noface.mp4");
Check(Near(Duration(noFace), 8, 0.15) && Streams(noFace).Contains("audio"), "face hidden, voice only");

string silent = Export(LessonExportKind.Blended, edit with { IncludeVoice = false, IncludeSystemAudio = false }, "silent.mp4");
Check(!Streams(silent).Contains("audio"), $"both sounds off -> no audio stream: {Streams(silent)}");

string face = Export(LessonExportKind.Face, edit, "face.mp4");
Check(Near(Duration(face), 8, 0.15) && Streams(face).Contains("video,640,480") && Streams(face).Contains("audio"), $"face track export: {Streams(face)}");
string screen = Export(LessonExportKind.Screen, edit, "screen.mp4");
Check(Near(Duration(screen), 8, 0.15) && !Streams(screen).Contains("audio"), "screen track export (silent)");
string voice = Export(LessonExportKind.Voice, edit, "voice.m4a");
Check(Near(Duration(voice), 8, 0.15) && !Streams(voice).Contains("video"), $"voice export: {Streams(voice)}");
string system = Export(LessonExportKind.SystemAudio, edit, "system.m4a");
Check(Near(Duration(system), 8, 0.15) && !Streams(system).Contains("video"), "system sound export");

string single = Export(LessonExportKind.Blended, edit with { Cuts = [] }, "single.mp4");
Check(Near(Duration(single), 10, 0.15), "single segment (no cuts) = 10 s");

string preview = Export(LessonExportKind.Blended, edit, "preview.mp4", preview: 6);
Check(Near(Duration(preview), 5, 0.2), $"preview from 6 s is clipped at trim end 11 -> 5 s: {Duration(preview):0.000}");
Check(Streams(preview).Contains("video,1280,720"), "preview video size");

// Audio content check: voice 440 Hz and system 880 Hz both present in the blend, at the chosen volumes.
string Stats(string path, string band)
{
    ProcessStartInfo info = new("ffmpeg") { RedirectStandardError = true };
    foreach (string a in new[] { "-v", "info", "-i", path, "-af", $"{band},astats=measure_perchannel=none:measure_overall=RMS_level", "-f", "null", "-" }) info.ArgumentList.Add(a);
    using Process p = Process.Start(info)!;
    string log = p.StandardError.ReadToEnd(); p.WaitForExit();
    return log.Split('\n').Last(l => l.Contains("RMS level dB")).Split(':').Last().Trim();
}
double rms440 = double.Parse(Stats(blended, "bandpass=f=440:width_type=q:w=8"), CultureInfo.InvariantCulture);
double rms880 = double.Parse(Stats(blended, "bandpass=f=880:width_type=q:w=8"), CultureInfo.InvariantCulture);
double voiceOnly880 = double.Parse(Stats(noFace, "bandpass=f=880:width_type=q:w=8"), CultureInfo.InvariantCulture);
Console.WriteLine($"blend RMS: 440 Hz {rms440} dB, 880 Hz {rms880} dB; voice-only export 880 Hz {voiceOnly880} dB");
Check(rms440 > -40 && rms880 > -40, "both voice and system sound are in the blend");
Check(Near(rms440 - rms880, 6, 2), "system sound at 0.5 volume is ~6 dB quieter than the voice");
double voiceReference880 = double.Parse(Stats(voice, "bandpass=f=880:width_type=q:w=8"), CultureInfo.InvariantCulture);
Check(Near(voiceOnly880, voiceReference880, 0.5), $"excluding system sound removes it (880 Hz band equals voice-only reference {voiceReference880} dB)");
Check(!File.ReadAllText(manifest).Contains("voiceSource"), "computed voiceSource is not written to the lesson file");

Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILED");
return failures == 0 ? 0 : 1;
