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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareX.ScreenRecordingLib;

/// <summary>The independently editable parts of a lesson recording.</summary>
public enum LessonTrackKind
{
    /// <summary>Screen video only: no camera overlay and no audio.</summary>
    Screen,
    /// <summary>Full-frame camera video, with the microphone when it was recorded.</summary>
    Camera,
    /// <summary>Microphone only, used when the microphone was recorded without a camera.</summary>
    Voice,
    /// <summary>System (loopback) audio only.</summary>
    SystemAudio
}

/// <param name="File">File name relative to the manifest's folder after commit; an absolute path before.</param>
public sealed record LessonTrack(LessonTrackKind Kind, string File, bool HasVideo, bool HasAudio, int Width = 0, int Height = 0);

/// <summary>Where the face appears in the blended lesson. Defaults come from the camera overlay settings.</summary>
public sealed record LessonFaceLayout
{
    public CameraOverlayPosition Position { get; init; } = CameraOverlayPosition.BottomRight;
    public CameraOverlayShape Shape { get; init; } = CameraOverlayShape.Rectangle;
    public int WidthPercent { get; init; } = 20;
    public int Margin { get; init; } = 16;
}

/// <summary>
/// A lesson recording: separate, time-aligned screen, face+voice and system-audio tracks that share
/// one media clock, so every track starts at time zero. Saved as &lt;name&gt;.lesson.json beside the screen video.
/// </summary>
public sealed record LessonSession
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public double DurationSeconds { get; init; }
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
    public IReadOnlyList<LessonTrack> Tracks { get; init; } = [];
    public LessonFaceLayout Face { get; init; } = new();
    /// <summary>The lesson editor's saved edits; null until the lesson has been edited.</summary>
    public LessonEdit? Edit { get; init; }

    public LessonTrack? Find(LessonTrackKind kind) => Tracks.FirstOrDefault(track => track.Kind == kind);

    /// <summary>The track that carries the teacher's voice: the camera track when it has audio, otherwise the voice track.</summary>
    [JsonIgnore]
    public LessonTrack? VoiceSource =>
        Tracks.FirstOrDefault(track => track.Kind == LessonTrackKind.Camera && track.HasAudio) ?? Find(LessonTrackKind.Voice);
}

/// <summary>Names, moves, saves and loads lesson track files.</summary>
public static class LessonFiles
{
    public const string ManifestSuffix = ".lesson.json";

    private static readonly JsonSerializerOptions json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Suffix(LessonTrackKind kind) => kind switch
    {
        LessonTrackKind.Camera => ".camera.mp4",
        LessonTrackKind.Voice => ".voice.m4a",
        LessonTrackKind.SystemAudio => ".system-audio.m4a",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "The screen track uses the recording's own path.")
    };

    /// <summary>Companion track path beside the screen video: "lesson.mp4" gives "lesson.camera.mp4".</summary>
    public static string TrackPath(string screenPath, LessonTrackKind kind) => kind == LessonTrackKind.Screen
        ? screenPath
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(screenPath))!, Path.GetFileNameWithoutExtension(screenPath) + Suffix(kind));

    public static string ManifestPath(string screenPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(screenPath))!, Path.GetFileNameWithoutExtension(screenPath) + ManifestSuffix);

    /// <summary>
    /// Move the companion tracks to the names that match the committed screen video and write the manifest.
    /// Call after the screen video has been moved to <paramref name="finalScreenPath"/>.
    /// Pre-existing files are replaced only when <paramref name="replaceExisting"/> is true.
    /// </summary>
    public static LessonSession Commit(LessonSession recorded, string finalScreenPath, bool replaceExisting)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        finalScreenPath = Path.GetFullPath(finalScreenPath);
        string manifest = ManifestPath(finalScreenPath);
        if (!replaceExisting && File.Exists(manifest))
            throw new IOException($"A lesson file already exists at '{manifest}'.");
        List<LessonTrack> tracks = new(recorded.Tracks.Count);
        foreach (LessonTrack track in recorded.Tracks)
        {
            string destination = TrackPath(finalScreenPath, track.Kind);
            string source = track.Kind == LessonTrackKind.Screen ? destination : track.File;
            if (track.Kind != LessonTrackKind.Screen && !string.Equals(Path.GetFullPath(source), destination, StringComparison.OrdinalIgnoreCase))
                File.Move(source, destination, replaceExisting);
            tracks.Add(track with { File = Path.GetFileName(destination) });
        }
        LessonSession committed = recorded with { Tracks = tracks };
        Save(committed, manifest);
        return committed;
    }

    /// <summary>Delete the companion files of an aborted or restarted recording. The screen video is the caller's.</summary>
    public static void DeleteCompanions(LessonSession? recorded)
    {
        if (recorded == null) return;
        foreach (LessonTrack track in recorded.Tracks)
            if (track.Kind != LessonTrackKind.Screen && File.Exists(track.File)) File.Delete(track.File);
    }

    public static void Save(LessonSession session, string manifestPath)
    {
        string temporary = manifestPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(session, json));
        File.Move(temporary, manifestPath, true);
    }

    public static LessonSession Load(string manifestPath)
    {
        LessonSession session = JsonSerializer.Deserialize<LessonSession>(File.ReadAllText(manifestPath), json)
            ?? throw new InvalidDataException("The lesson file is empty.");
        if (session.Version is < 1 or > LessonSession.CurrentVersion)
            throw new InvalidDataException($"Lesson file version {session.Version} is not supported.");
        if (session.Find(LessonTrackKind.Screen) == null)
            throw new InvalidDataException("The lesson file has no screen track.");
        return session;
    }

    /// <summary>Absolute path of a committed track, resolved against the manifest's folder.</summary>
    public static string Resolve(string manifestPath, LessonTrack track) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, track.File));
}
