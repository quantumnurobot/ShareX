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

namespace ShareX.ScreenRecordingLib;

public enum LessonFacePlacement { TopLeft, TopRight, BottomLeft, BottomRight, Custom }

/// <summary>A removed section of the lesson, in recording time.</summary>
public sealed record LessonCut(double Start, double End);

/// <summary>A kept section of the lesson, in recording time.</summary>
public readonly record struct LessonSegment(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>Non-destructive lesson edits. The recorded track files are never modified.</summary>
public sealed record LessonEdit
{
    public const double MinimumSegment = 0.05;

    public double TrimStart { get; init; }
    /// <summary>Null keeps everything to the end of the recording.</summary>
    public double? TrimEnd { get; init; }
    public IReadOnlyList<LessonCut> Cuts { get; init; } = [];

    public bool ShowFace { get; init; } = true;
    public LessonFacePlacement FacePlacement { get; init; } = LessonFacePlacement.BottomRight;
    /// <summary>Custom placement, 0 (left) to 1 (right) of the space beside the face.</summary>
    public double FaceX { get; init; } = 1;
    /// <summary>Custom placement, 0 (top) to 1 (bottom) of the space beside the face.</summary>
    public double FaceY { get; init; } = 1;
    public int FaceWidthPercent { get; init; } = 20;
    public CameraOverlayShape FaceShape { get; init; } = CameraOverlayShape.Rectangle;
    public int FaceMargin { get; init; } = 16;

    public bool IncludeVoice { get; init; } = true;
    /// <summary>Linear gain, 0 to 4.</summary>
    public double VoiceVolume { get; init; } = 1;
    public bool IncludeSystemAudio { get; init; } = true;
    /// <summary>Linear gain, 0 to 4.</summary>
    public double SystemVolume { get; init; } = 1;

    /// <summary>Start from the face layout the lesson was recorded with.</summary>
    public static LessonEdit FromSession(LessonSession session) => session.Edit ?? new()
    {
        FacePlacement = session.Face.Position switch
        {
            CameraOverlayPosition.TopLeft => LessonFacePlacement.TopLeft,
            CameraOverlayPosition.TopRight => LessonFacePlacement.TopRight,
            CameraOverlayPosition.BottomLeft => LessonFacePlacement.BottomLeft,
            _ => LessonFacePlacement.BottomRight
        },
        FaceShape = session.Face.Shape,
        FaceWidthPercent = session.Face.WidthPercent,
        FaceMargin = session.Face.Margin
    };

    /// <summary>Clamp every value into its valid range for a lesson of <paramref name="duration"/> seconds.</summary>
    public LessonEdit Normalize(double duration)
    {
        duration = Math.Max(0, double.IsFinite(duration) ? duration : 0);
        double start = Math.Clamp(Finite(TrimStart), 0, duration);
        double end = Math.Clamp(TrimEnd is double value ? Finite(value, duration) : duration, start, duration);
        return this with
        {
            TrimStart = start,
            TrimEnd = end >= duration - 0.0005 ? null : end,
            Cuts = MergeCuts(Cuts, duration),
            FaceX = Math.Clamp(Finite(FaceX, 1), 0, 1),
            FaceY = Math.Clamp(Finite(FaceY, 1), 0, 1),
            FaceWidthPercent = Math.Clamp(FaceWidthPercent, 5, 60),
            FaceMargin = Math.Clamp(FaceMargin, 0, 1000),
            VoiceVolume = Math.Clamp(Finite(VoiceVolume, 1), 0, 4),
            SystemVolume = Math.Clamp(Finite(SystemVolume, 1), 0, 4)
        };
    }

    /// <summary>The sections kept after trimming and cutting, in order. Slivers shorter than 50 ms are dropped.</summary>
    public IReadOnlyList<LessonSegment> KeptSegments(double duration)
    {
        LessonEdit edit = Normalize(duration);
        double end = edit.TrimEnd ?? Math.Max(0, duration);
        List<LessonSegment> kept = [];
        double cursor = edit.TrimStart;
        foreach (LessonCut cut in edit.Cuts)
        {
            if (cut.End <= cursor) continue;
            if (cut.Start >= end) break;
            if (cut.Start > cursor) Add(cursor, Math.Min(cut.Start, end));
            cursor = Math.Max(cursor, cut.End);
        }
        Add(cursor, end);
        return kept;

        void Add(double from, double to)
        {
            if (to - from >= MinimumSegment) kept.Add(new(from, to));
        }
    }

    /// <summary>Total length of the edited lesson.</summary>
    public double EditedDuration(double duration) => KeptSegments(duration).Sum(segment => segment.Length);

    private static IReadOnlyList<LessonCut> MergeCuts(IEnumerable<LessonCut> cuts, double duration)
    {
        List<LessonCut> merged = [];
        foreach (LessonCut cut in cuts
            .Select(cut => new LessonCut(Math.Clamp(Finite(Math.Min(cut.Start, cut.End)), 0, duration), Math.Clamp(Finite(Math.Max(cut.Start, cut.End)), 0, duration)))
            .Where(cut => cut.End - cut.Start > 0.0005)
            .OrderBy(cut => cut.Start))
        {
            if (merged.Count > 0 && cut.Start <= merged[^1].End) merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, cut.End) };
            else merged.Add(cut);
        }
        return merged;
    }

    private static double Finite(double value, double fallback = 0) => double.IsFinite(value) ? value : fallback;
}
