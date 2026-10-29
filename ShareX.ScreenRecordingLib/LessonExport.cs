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

using System.Globalization;
using System.Text;

namespace ShareX.ScreenRecordingLib;

public enum LessonExportKind
{
    /// <summary>Screen with the face on top, voice and system sound mixed: the finished lesson.</summary>
    Blended,
    /// <summary>The screen video alone (silent).</summary>
    Screen,
    /// <summary>The face video with the voice.</summary>
    Face,
    /// <summary>The voice alone (.m4a).</summary>
    Voice,
    /// <summary>The system sound alone (.m4a).</summary>
    SystemAudio
}

/// <summary>Builds FFmpeg command lines that apply a <see cref="LessonEdit"/> to the recorded tracks.</summary>
public static class LessonExport
{
    private static readonly CultureInfo invariant = CultureInfo.InvariantCulture;

    public static bool IsAudioOnly(LessonExportKind kind) => kind is LessonExportKind.Voice or LessonExportKind.SystemAudio;
    public static string Extension(LessonExportKind kind) => IsAudioOnly(kind) ? ".m4a" : ".mp4";

    /// <summary>Whether the lesson has the tracks this export needs.</summary>
    public static bool CanExport(LessonSession session, LessonExportKind kind) => kind switch
    {
        LessonExportKind.Face => session.Find(LessonTrackKind.Camera) != null,
        LessonExportKind.Voice => session.VoiceSource != null,
        LessonExportKind.SystemAudio => session.Find(LessonTrackKind.SystemAudio) != null,
        _ => session.Find(LessonTrackKind.Screen) != null
    };

    /// <param name="manifestPath">The .lesson.json file; track paths are resolved beside it.</param>
    /// <param name="previewStart">Preview only: render the edited lesson starting from this recording time.</param>
    /// <param name="previewLength">Preview only: maximum length in seconds, rendered fast at reduced size.</param>
    public static string[] BuildArguments(LessonSession session, string manifestPath, LessonEdit edit, LessonExportKind kind,
        string output, double? previewStart = null, double previewLength = 10)
    {
        if (!CanExport(session, kind)) throw new InvalidOperationException($"This lesson has no track for a {kind} export.");
        bool preview = previewStart.HasValue;
        edit = edit.Normalize(session.DurationSeconds);
        if (preview)
        {
            // A preview is the same edit, limited to a short window of recording time.
            double from = Math.Clamp(previewStart!.Value, edit.TrimStart, edit.TrimEnd ?? session.DurationSeconds);
            edit = edit with { TrimStart = from, TrimEnd = Math.Min(edit.TrimEnd ?? session.DurationSeconds, from + Math.Max(1, previewLength)) };
        }
        IReadOnlyList<LessonSegment> segments = edit.KeptSegments(session.DurationSeconds);
        if (segments.Count == 0) throw new InvalidOperationException("Nothing is left to export: the trim and cuts remove the whole lesson.");

        // Seek every input by the same amount so the shared clock stays aligned, then shift the segments.
        double seek = Math.Max(0, segments[0].Start - 1);
        List<string> inputs = [];
        int Input(LessonTrack track)
        {
            string path = LessonFiles.Resolve(manifestPath, track);
            int index = inputs.IndexOf(path);
            if (index >= 0) return index;
            inputs.Add(path);
            return inputs.Count - 1;
        }

        LessonTrack screen = session.Find(LessonTrackKind.Screen)!;
        LessonTrack? camera = session.Find(LessonTrackKind.Camera);
        LessonTrack? voice = session.VoiceSource;
        LessonTrack? system = session.Find(LessonTrackKind.SystemAudio);
        List<string> graph = [];
        string? video = null, audio = null;

        switch (kind)
        {
            case LessonExportKind.Blended:
            {
                int screenInput = Input(screen);
                string screenLabel = $"[{screenInput}:v]";
                if (edit.ShowFace && camera != null)
                {
                    int cameraInput = Input(camera);
                    graph.Add($"[{cameraInput}:v]{FaceFilter(session, edit)}[face]");
                    graph.Add($"{screenLabel}[face]overlay={FacePosition(edit)}:eof_action=pass[composed]");
                    screenLabel = "[composed]";
                }
                graph.Add($"{screenLabel}{EvenSize(preview)}[vfull]");
                video = "[vfull]";
                audio = MixAudio(graph, edit.IncludeVoice && voice != null ? (Input(voice), edit.VoiceVolume) : null,
                    edit.IncludeSystemAudio && system != null ? (Input(system), edit.SystemVolume) : null);
                break;
            }
            case LessonExportKind.Screen:
                graph.Add($"[{Input(screen)}:v]{EvenSize(preview)}[vfull]");
                video = "[vfull]";
                break;
            case LessonExportKind.Face:
            {
                int cameraInput = Input(camera!);
                graph.Add($"[{cameraInput}:v]{EvenSize(preview)}[vfull]");
                video = "[vfull]";
                if (camera!.HasAudio) audio = MixAudio(graph, (cameraInput, edit.VoiceVolume), null);
                break;
            }
            case LessonExportKind.Voice:
                audio = MixAudio(graph, (Input(voice!), edit.VoiceVolume), null);
                break;
            case LessonExportKind.SystemAudio:
                audio = MixAudio(graph, (Input(system!), edit.SystemVolume), null);
                break;
        }

        AppendSegments(graph, segments, seek, video, audio);

        List<string> args = ["-v", "error"];
        foreach (string input in inputs)
        {
            if (seek > 0) args.AddRange(["-ss", Number(seek)]);
            args.AddRange(["-i", input]);
        }
        args.AddRange(["-filter_complex", string.Join(";", graph)]);
        if (video != null)
        {
            args.AddRange(["-map", "[vout]", "-c:v", "libx264", "-preset", preview ? "ultrafast" : "medium",
                "-crf", preview ? "28" : "20", "-pix_fmt", "yuv420p"]);
        }
        if (audio != null) args.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", preview ? "128k" : "192k"]);
        args.AddRange(["-movflags", "+faststart", "-progress", "pipe:1", "-nostats", "-y", output]);
        return args.ToArray();
    }

    /// <summary>Scale (and for a circle, crop and mask) the camera to its on-screen size.</summary>
    internal static string FaceFilter(LessonSession session, LessonEdit edit)
    {
        int width = Math.Max(2, (int)Math.Round(session.ScreenWidth * edit.FaceWidthPercent / 100d) & ~1);
        if (edit.FaceShape != CameraOverlayShape.Circle) return $"scale={width}:-2";
        // A centered square crop with an alpha mask; the overlay blends the corners away.
        return $"crop='min(iw,ih)':'min(iw,ih)',scale={width}:{width},format=yuva420p," +
            "geq=lum='p(X,Y)':cb='p(X,Y)':cr='p(X,Y)':a='if(lte(hypot(X-W/2,Y-H/2),W/2),255,0)'";
    }

    internal static string FacePosition(LessonEdit edit)
    {
        string m = edit.FaceMargin.ToString(invariant);
        (string x, string y) = edit.FacePlacement switch
        {
            LessonFacePlacement.TopLeft => (m, m),
            LessonFacePlacement.TopRight => ($"main_w-overlay_w-{m}", m),
            LessonFacePlacement.BottomLeft => (m, $"main_h-overlay_h-{m}"),
            LessonFacePlacement.Custom => ($"(main_w-overlay_w)*{Number(edit.FaceX)}", $"(main_h-overlay_h)*{Number(edit.FaceY)}"),
            _ => ($"main_w-overlay_w-{m}", $"main_h-overlay_h-{m}")
        };
        return $"x={x}:y={y}";
    }

    private static string EvenSize(bool preview) => preview
        ? "scale='min(1280,iw)':-2,pad=ceil(iw/2)*2:ceil(ih/2)*2,format=yuv420p"
        : "pad=ceil(iw/2)*2:ceil(ih/2)*2,format=yuv420p";

    private static string? MixAudio(List<string> graph, (int Input, double Volume)? voice, (int Input, double Volume)? system)
    {
        List<string> sources = [];
        if (voice is { } v) { graph.Add($"[{v.Input}:a]volume={Number(v.Volume)}[voice]"); sources.Add("[voice]"); }
        if (system is { } s) { graph.Add($"[{s.Input}:a]volume={Number(s.Volume)}[system]"); sources.Add("[system]"); }
        if (sources.Count == 0) return null;
        // Mix at the chosen volumes; normalize=0 keeps amix from halving each source.
        graph.Add(sources.Count == 1
            ? $"{sources[0]}aresample=48000[afull]"
            : $"{sources[0]}{sources[1]}amix=inputs=2:duration=longest:normalize=0,aresample=48000[afull]");
        return "[afull]";
    }

    private static void AppendSegments(List<string> graph, IReadOnlyList<LessonSegment> segments, double seek, string? video, string? audio)
    {
        int count = segments.Count;
        string Range(LessonSegment segment) => $"start={Number(segment.Start - seek)}:end={Number(segment.End - seek)}";
        if (count == 1)
        {
            if (video != null) graph.Add($"{video}trim={Range(segments[0])},setpts=PTS-STARTPTS[vout]");
            if (audio != null) graph.Add($"{audio}atrim={Range(segments[0])},asetpts=PTS-STARTPTS[aout]");
            return;
        }
        StringBuilder concat = new();
        if (video != null) graph.Add($"{video}split={count}{Labels("vcopy", count)}");
        if (audio != null) graph.Add($"{audio}asplit={count}{Labels("acopy", count)}");
        for (int i = 0; i < count; i++)
        {
            if (video != null)
            {
                graph.Add($"[vcopy{i}]trim={Range(segments[i])},setpts=PTS-STARTPTS[vpart{i}]");
                concat.Append($"[vpart{i}]");
            }
            if (audio != null)
            {
                graph.Add($"[acopy{i}]atrim={Range(segments[i])},asetpts=PTS-STARTPTS[apart{i}]");
                concat.Append($"[apart{i}]");
            }
        }
        concat.Append($"concat=n={count}:v={(video != null ? 1 : 0)}:a={(audio != null ? 1 : 0)}");
        if (video != null) concat.Append("[vout]");
        if (audio != null) concat.Append("[aout]");
        graph.Add(concat.ToString());
    }

    private static string Labels(string name, int count) => string.Concat(Enumerable.Range(0, count).Select(i => $"[{name}{i}]"));
    private static string Number(double value) => value.ToString("0.######", invariant);
}
