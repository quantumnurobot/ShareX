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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShareX.ScreenRecordingLib;
using System.Collections.ObjectModel;

namespace ShareX.Tools;

/// <summary>
/// Edits a lesson recording non-destructively: trim, cut sections, place the face, set each sound's volume,
/// then blend the tracks (or export any one of them) with FFmpeg. Edits are saved in the .lesson.json file.
/// </summary>
public sealed partial class LessonEditorViewModel : ViewModelBase, IDisposable
{
    private const double SyncTolerance = 0.25;
    private readonly LessonExportService _service;
    private readonly WindowsMediaPlayer _screenPlayer;
    private readonly WindowsMediaPlayer _facePlayer;
    private readonly Func<string?>? _resolveFFmpegPath;
    private readonly Action? _playNotificationSound;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _workCancellation;
    private LessonSession? _session;
    private bool _disposed, _updatingPosition, _hasFace, _loadingEdit;
    private double _facePosition;

    [ObservableProperty] private string _lessonFilePath = string.Empty;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _start;
    [ObservableProperty] private double _end;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "Open a lesson recording (.lesson.json).";
    [ObservableProperty] private string _outputFilePath = string.Empty;
    [ObservableProperty] private double _markIn = -1;

    [ObservableProperty] private bool _showFace = true;
    [ObservableProperty] private int _facePlacementIndex = (int)LessonFacePlacement.BottomRight;
    [ObservableProperty] private int _faceShapeIndex;
    [ObservableProperty] private double _faceWidthPercent = 20;
    [ObservableProperty] private double _faceMargin = 16;
    [ObservableProperty] private double _faceX = 1;
    [ObservableProperty] private double _faceY = 1;
    [ObservableProperty] private bool _includeVoice = true;
    [ObservableProperty] private double _voiceVolumePercent = 100;
    [ObservableProperty] private bool _includeSystemAudio = true;
    [ObservableProperty] private double _systemVolumePercent = 100;
    [ObservableProperty] private int _trackExportIndex;

    public LessonEditorViewModel(string ffmpegPath, WindowsMediaPlayer screenPlayer, WindowsMediaPlayer facePlayer,
        Action? playNotificationSound = null, Func<string?>? resolveFFmpegPath = null)
    {
        _service = new(ffmpegPath);
        _screenPlayer = screenPlayer;
        _facePlayer = facePlayer;
        _playNotificationSound = playNotificationSound;
        _resolveFFmpegPath = resolveFFmpegPath;
        _screenPlayer.IsMuted = true; // The screen track is silent; the voice plays from the face track.
        _screenPlayer.PositionChanged += UpdatePlaybackPosition;
        _screenPlayer.IsPlayingChanged += UpdatePlaybackState;
        _screenPlayer.PlaybackFailed += HandlePlaybackFailure;
        _facePlayer.PositionChanged += position => _facePosition = position;
    }

    public Func<Task<string?>>? SelectLessonRequested { get; set; }
    public Func<string, string, Task<string?>>? SelectOutputRequested { get; set; }
    public Action<string>? ShowErrorRequested { get; set; }
    public Action<string>? OpenPreviewRequested { get; set; }

    public ObservableCollection<LessonCut> Cuts { get; } = [];
    public LessonSession? Session => _session;
    public bool HasLesson => _session != null && Duration > 0;
    public bool HasFaceTrack => _hasFace;
    public bool HasVoice => _session?.VoiceSource != null;
    public bool HasSystemAudio => _session?.Find(LessonTrackKind.SystemAudio) != null;
    public bool CanEdit => HasLesson && !IsExporting;
    public bool CanBrowse => !IsExporting;
    public bool IsWorking => IsLoading || IsExporting;
    public bool HasStatus => !string.IsNullOrEmpty(StatusText);
    public bool HasOutput => !string.IsNullOrEmpty(OutputFilePath);
    public bool HasMarkIn => MarkIn >= 0;
    public bool IsCustomPlacement => FacePlacementIndex == (int)LessonFacePlacement.Custom;
    public string PlayActionText => IsPlaying ? "Pause" : "Play";
    public string PositionText => FormatTime(Position);
    public string DurationText => FormatTime(Duration);
    public string StartTimeText => FormatTime(Start);
    public string EndTimeText => FormatTime(End);
    public string EditedDurationText => HasLesson ? FormatTime(CurrentEdit().EditedDuration(Duration)) : FormatTime(0);
    public string LessonDisplay => string.IsNullOrEmpty(LessonFilePath) ? "No lesson open" : LessonFilePath;
    public string CutButtonText => HasMarkIn ? $"Cut from {FormatTime(MarkIn)} to here" : "Start cut here";
    public string TracksText => _session == null ? string.Empty : string.Join("   •   ", _session.Tracks.Select(track => track.Kind switch
    {
        LessonTrackKind.Screen => $"Screen {track.Width}×{track.Height}",
        LessonTrackKind.Camera => track.HasAudio ? $"Face + voice {track.Width}×{track.Height}" : $"Face {track.Width}×{track.Height}",
        LessonTrackKind.Voice => "Voice",
        _ => "System sound"
    }));
    public IReadOnlyList<string> PlacementNames { get; } = ["Top left", "Top right", "Bottom left", "Bottom right", "Custom (drag the face)"];
    public IReadOnlyList<string> ShapeNames { get; } = ["Rectangle", "Circle"];
    public IReadOnlyList<string> TrackExportNames { get; } = ["Screen only (silent)", "Face + voice", "Voice only (.m4a)", "System sound only (.m4a)"];
    private static readonly LessonExportKind[] trackExportKinds =
        [LessonExportKind.Screen, LessonExportKind.Face, LessonExportKind.Voice, LessonExportKind.SystemAudio];

    public static string FormatTime(double seconds)
    {
        TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    /// <summary>The current edit, from the editor's controls.</summary>
    public LessonEdit CurrentEdit() => new LessonEdit
    {
        TrimStart = Start,
        TrimEnd = End,
        Cuts = Cuts.ToArray(),
        ShowFace = ShowFace,
        FacePlacement = (LessonFacePlacement)Math.Clamp(FacePlacementIndex, 0, 4),
        FaceShape = FaceShapeIndex == 1 ? CameraOverlayShape.Circle : CameraOverlayShape.Rectangle,
        FaceWidthPercent = (int)Math.Round(FaceWidthPercent),
        FaceMargin = (int)Math.Round(FaceMargin),
        FaceX = FaceX,
        FaceY = FaceY,
        IncludeVoice = IncludeVoice,
        VoiceVolume = VoiceVolumePercent / 100,
        IncludeSystemAudio = IncludeSystemAudio,
        SystemVolume = SystemVolumePercent / 100
    }.Normalize(Duration);

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (!CanBrowse || SelectLessonRequested == null) return;
        try
        {
            string? file = await SelectLessonRequested();
            if (file != null && !_disposed) await LoadLessonAsync(file);
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    public async Task LoadLessonAsync(string file)
    {
        if (!CanBrowse || _disposed) return;
        SaveEdit();
        _screenPlayer.Unload();
        _facePlayer.Unload();
        _session = null;
        _hasFace = false;
        Duration = 0;
        Position = Start = End = 0;
        Cuts.Clear();
        MarkIn = -1;
        OutputFilePath = string.Empty;
        IsLoading = true;
        StatusText = "Loading lesson…";
        try
        {
            // Accept the screen video too: find its lesson file beside it.
            if (!file.EndsWith(LessonFiles.ManifestSuffix, StringComparison.OrdinalIgnoreCase)) file = LessonFiles.ManifestPath(file);
            if (!File.Exists(file)) throw new FileNotFoundException("This is not a lesson recording. Choose a .lesson.json file (record with lesson mode on).");
            LessonSession session = LessonFiles.Load(file);
            foreach (LessonTrack track in session.Tracks)
            {
                string path = LessonFiles.Resolve(file, track);
                if (!File.Exists(path)) throw new FileNotFoundException($"The lesson's {track.Kind} track is missing: {path}");
            }
            LessonFilePath = file;
            _session = session;
            VideoPlaybackInfo screen = await _screenPlayer.LoadAsync(LessonFiles.Resolve(file, session.Find(LessonTrackKind.Screen)!), _lifetime.Token);
            if (session.Find(LessonTrackKind.Camera) is { } camera)
            {
                try
                {
                    await _facePlayer.LoadAsync(LessonFiles.Resolve(file, camera), _lifetime.Token);
                    _hasFace = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    StatusText = "The face track could not be played here, but it will still be exported. " + ex.Message;
                }
            }
            double duration = session.DurationSeconds > 0 ? Math.Min(session.DurationSeconds, Math.Max(screen.Duration, 0.1)) : screen.Duration;
            ApplyEdit(LessonEdit.FromSession(session), duration);
            OnLessonChanged();
            if (StatusText == "Loading lesson…") StatusText = string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _session = null;
            Duration = 0;
            StatusText = "Could not open the lesson: " + ex.Message;
            ShowErrorRequested?.Invoke(StatusText);
            OnLessonChanged();
        }
        finally { IsLoading = false; }
    }

    private void ApplyEdit(LessonEdit edit, double duration)
    {
        _loadingEdit = true;
        try
        {
            edit = edit.Normalize(duration);
            Duration = duration;
            End = edit.TrimEnd ?? duration;
            Start = edit.TrimStart;
            Cuts.Clear();
            foreach (LessonCut cut in edit.Cuts) Cuts.Add(cut);
            ShowFace = edit.ShowFace;
            FacePlacementIndex = (int)edit.FacePlacement;
            FaceShapeIndex = edit.FaceShape == CameraOverlayShape.Circle ? 1 : 0;
            FaceWidthPercent = edit.FaceWidthPercent;
            FaceMargin = edit.FaceMargin;
            FaceX = edit.FaceX;
            FaceY = edit.FaceY;
            IncludeVoice = edit.IncludeVoice;
            VoiceVolumePercent = edit.VoiceVolume * 100;
            IncludeSystemAudio = edit.IncludeSystemAudio;
            SystemVolumePercent = edit.SystemVolume * 100;
            Position = Start;
        }
        finally { _loadingEdit = false; }
        UpdateFaceAudio();
    }

    private void OnLessonChanged()
    {
        foreach (string name in new[] { nameof(Session), nameof(HasLesson), nameof(HasFaceTrack), nameof(HasVoice), nameof(HasSystemAudio),
            nameof(CanEdit), nameof(TracksText), nameof(EditedDurationText) })
            OnPropertyChanged(name);
    }

    /// <summary>Save the edits into the lesson file. The recorded tracks are never changed.</summary>
    public void SaveEdit()
    {
        if (_session == null || string.IsNullOrEmpty(LessonFilePath) || !HasLesson) return;
        try
        {
            _session = _session with { Edit = CurrentEdit() };
            LessonFiles.Save(_session, LessonFilePath);
        }
        catch (Exception ex) { StatusText = "Edits could not be saved: " + ex.Message; }
    }

    // ---------- Playback (the screen track is the clock; the face follows it) ----------

    partial void OnPositionChanged(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > Duration)
        {
            Position = double.IsFinite(value) ? Math.Clamp(value, 0, Duration) : 0;
            return;
        }
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(CutButtonText));
        if (HasLesson && !_disposed && !_updatingPosition)
        {
            _screenPlayer.Seek(value);
            if (_hasFace) _facePlayer.Seek(value);
        }
    }

    private void UpdatePlaybackPosition(double position)
    {
        if (_disposed || !HasLesson) return;
        if (IsPlaying)
        {
            if (position >= End)
            {
                PausePlayers();
                SetPositionFromPlayback(End);
                return;
            }
            // Skip cut sections during playback, as they will be skipped in the export.
            if (Cuts.FirstOrDefault(cut => position >= cut.Start && position < cut.End) is { } cut)
            {
                double resume = Math.Min(cut.End, End);
                SeekPlayers(resume);
                SetPositionFromPlayback(resume);
                PlayPlayers();
                return;
            }
            if (_hasFace && Math.Abs(_facePosition - position) > SyncTolerance)
            {
                _facePlayer.Seek(position);
                _facePlayer.Play();
            }
        }
        SetPositionFromPlayback(position);
    }

    private void SetPositionFromPlayback(double position)
    {
        _updatingPosition = true;
        try { Position = Math.Clamp(position, 0, Duration); }
        finally { _updatingPosition = false; }
    }

    private void UpdatePlaybackState(bool playing)
    {
        IsPlaying = playing;
        if (!playing && _hasFace) _facePlayer.Pause();
    }

    private void HandlePlaybackFailure(Exception error)
    {
        if (_disposed) return;
        StatusText = "Playback error: " + error.Message;
        ShowErrorRequested?.Invoke(StatusText);
    }

    private void SeekPlayers(double position)
    {
        _screenPlayer.Seek(position);
        if (_hasFace) _facePlayer.Seek(position);
    }

    private void PlayPlayers()
    {
        _screenPlayer.Play();
        if (_hasFace) _facePlayer.Play();
    }

    private void PausePlayers()
    {
        _screenPlayer.Pause();
        if (_hasFace) _facePlayer.Pause();
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (!CanEdit) return;
        if (IsPlaying) { PausePlayers(); return; }
        if (Position < Start || Position >= End - 0.001) Position = Start;
        SeekPlayers(Position);
        PlayPlayers();
    }

    [RelayCommand] private void StepBackward() { if (CanEdit) { PausePlayers(); Position = Math.Max(0, Position - 1d / 30); } }
    [RelayCommand] private void StepForward() { if (CanEdit) { PausePlayers(); Position = Math.Min(Duration, Position + 1d / 30); } }

    // ---------- Trim and cuts ----------

    [RelayCommand] private void SetStart() { if (CanEdit) Start = Position; }
    [RelayCommand] private void SetEnd() { if (CanEdit) End = Position; }

    [RelayCommand]
    private void MarkCut()
    {
        if (!CanEdit) return;
        if (!HasMarkIn) { MarkIn = Position; return; }
        double from = Math.Min(MarkIn, Position), to = Math.Max(MarkIn, Position);
        MarkIn = -1;
        if (to - from < 0.05) return;
        LessonEdit merged = CurrentEdit() with { Cuts = [.. Cuts, new LessonCut(from, to)] };
        Cuts.Clear();
        foreach (LessonCut cut in merged.Normalize(Duration).Cuts) Cuts.Add(cut);
        EditChanged();
    }

    [RelayCommand] private void CancelMark() => MarkIn = -1;

    [RelayCommand]
    private void RemoveCut(LessonCut? cut)
    {
        if (cut == null || !CanEdit) return;
        Cuts.Remove(cut);
        EditChanged();
    }

    [RelayCommand]
    private void ResetEdits()
    {
        if (_session == null || !CanEdit) return;
        ApplyEdit(LessonEdit.FromSession(_session with { Edit = null }), Duration);
        EditChanged();
    }

    partial void OnStartChanged(double value)
    {
        double clamped = double.IsFinite(value) ? Math.Clamp(value, 0, Math.Max(0, End - 0.05)) : 0;
        if (value != clamped && Duration > 0) { Start = clamped; return; }
        OnPropertyChanged(nameof(StartTimeText));
        if (!_loadingEdit && HasLesson) Position = Start;
        EditChanged();
    }

    partial void OnEndChanged(double value)
    {
        double clamped = double.IsFinite(value) ? Math.Clamp(value, Math.Min(Duration, Start + 0.05), Duration) : Duration;
        if (value != clamped && Duration > 0) { End = clamped; return; }
        OnPropertyChanged(nameof(EndTimeText));
        if (!_loadingEdit && HasLesson) Position = End;
        EditChanged();
    }

    // ---------- Face and sound ----------

    /// <summary>Called by the layout preview when the face is dragged: switches to a custom placement.</summary>
    public void MoveFace(double x, double y)
    {
        if (!CanEdit) return;
        FacePlacementIndex = (int)LessonFacePlacement.Custom;
        FaceX = Math.Clamp(x, 0, 1);
        FaceY = Math.Clamp(y, 0, 1);
    }

    partial void OnFacePlacementIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomPlacement));
        EditChanged();
    }

    partial void OnShowFaceChanged(bool value) => EditChanged();
    partial void OnFaceShapeIndexChanged(int value) => EditChanged();
    partial void OnFaceWidthPercentChanged(double value) => EditChanged();
    partial void OnFaceMarginChanged(double value) => EditChanged();
    partial void OnFaceXChanged(double value) => EditChanged();
    partial void OnFaceYChanged(double value) => EditChanged();
    partial void OnIncludeSystemAudioChanged(bool value) => EditChanged();
    partial void OnSystemVolumePercentChanged(double value) => EditChanged();
    partial void OnIncludeVoiceChanged(bool value) { UpdateFaceAudio(); EditChanged(); }
    partial void OnVoiceVolumePercentChanged(double value) { UpdateFaceAudio(); EditChanged(); }

    private void UpdateFaceAudio()
    {
        if (_disposed) return;
        // The face player carries the voice. Preview levels above 100% are heard at 100%; the export applies the full gain.
        _facePlayer.IsMuted = !IncludeVoice;
        _facePlayer.Volume = Math.Clamp(VoiceVolumePercent / 100, 0, 1);
    }

    /// <summary>Raised whenever the edit changes, so the timeline and layout preview redraw.</summary>
    public event Action? EditUpdated;

    private void EditChanged()
    {
        if (_loadingEdit) return;
        OnPropertyChanged(nameof(EditedDurationText));
        EditUpdated?.Invoke();
    }

    partial void OnMarkInChanged(double value)
    {
        OnPropertyChanged(nameof(HasMarkIn));
        OnPropertyChanged(nameof(CutButtonText));
        EditUpdated?.Invoke();
    }

    partial void OnDurationChanged(double value)
    {
        OnPropertyChanged(nameof(DurationText));
        OnLessonChanged();
    }

    partial void OnLessonFilePathChanged(string value) => OnPropertyChanged(nameof(LessonDisplay));
    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayActionText));
    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));
    partial void OnOutputFilePathChanged(string value) => OnPropertyChanged(nameof(HasOutput));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsWorking));
    partial void OnIsExportingChanged(bool value)
    {
        if (value) PausePlayers();
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanBrowse));
        OnPropertyChanged(nameof(IsWorking));
    }

    // ---------- Preview and export ----------

    [RelayCommand]
    private Task PreviewAsync()
    {
        string output = Path.Combine(Path.GetTempPath(), $"ShareX-lesson-preview-{Guid.NewGuid():N}.mp4");
        return RunExportAsync(LessonExportKind.Blended, output, preview: true);
    }

    [RelayCommand]
    private async Task ExportLessonAsync()
    {
        if (!CanEdit || SelectOutputRequested == null) return;
        string? output = await SelectOutputRequested(SuggestedName("-lesson.mp4"), ".mp4");
        if (output != null) await RunExportAsync(LessonExportKind.Blended, output, preview: false);
    }

    [RelayCommand]
    private async Task ExportTrackAsync()
    {
        if (!CanEdit || SelectOutputRequested == null || _session == null) return;
        LessonExportKind kind = trackExportKinds[Math.Clamp(TrackExportIndex, 0, trackExportKinds.Length - 1)];
        if (!LessonExport.CanExport(_session, kind))
        {
            ShowErrorRequested?.Invoke("This lesson was recorded without that track.");
            return;
        }
        string suffix = kind switch
        {
            LessonExportKind.Screen => "-screen.mp4",
            LessonExportKind.Face => "-face.mp4",
            LessonExportKind.Voice => "-voice.m4a",
            _ => "-system-sound.m4a"
        };
        string? output = await SelectOutputRequested(SuggestedName(suffix), LessonExport.Extension(kind));
        if (output != null) await RunExportAsync(kind, output, preview: false);
    }

    private string SuggestedName(string suffix)
    {
        string name = Path.GetFileName(LessonFilePath);
        if (name.EndsWith(LessonFiles.ManifestSuffix, StringComparison.OrdinalIgnoreCase)) name = name[..^LessonFiles.ManifestSuffix.Length];
        return name + suffix;
    }

    private async Task RunExportAsync(LessonExportKind kind, string output, bool preview)
    {
        if (!CanEdit || _session == null || _disposed) return;
        LessonEdit edit = CurrentEdit();
        if (edit.KeptSegments(Duration).Count == 0)
        {
            ShowErrorRequested?.Invoke("Nothing is left to export: the trim and cuts remove the whole lesson.");
            return;
        }
        SaveEdit();
        IsExporting = true;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _workCancellation = cancellation;
        string? error = null;
        try
        {
            if (_resolveFFmpegPath != null)
            {
                string? ffmpegPath = _resolveFFmpegPath();
                if (string.IsNullOrEmpty(ffmpegPath)) return;
                _service.FFmpegPath = ffmpegPath;
            }
            Progress = 0;
            StatusText = preview ? "Rendering a 10-second preview from the playhead…" : "Exporting…";
            double previewStart = Position < edit.TrimStart || Position >= (edit.TrimEnd ?? Duration) ? edit.TrimStart : Position;
            await _service.ExportAsync(_session, LessonFilePath, edit, kind, output,
                new Progress<double>(value => { if (!cancellation.IsCancellationRequested && !_disposed) Progress = value; }),
                cancellation.Token, preview ? previewStart : null);
            Progress = 100;
            if (preview)
            {
                StatusText = "Preview ready.";
                OpenPreviewRequested?.Invoke(output);
            }
            else
            {
                OutputFilePath = output;
                StatusText = "Saved " + output;
                _playNotificationSound?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            Progress = 0;
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            Progress = 0;
            StatusText = ex.Message;
            if (!cancellation.IsCancellationRequested) error = ex.Message;
        }
        finally
        {
            _workCancellation = null;
            IsExporting = false;
        }
        if (error != null && !_disposed) ShowErrorRequested?.Invoke(error);
    }

    [RelayCommand] private void Cancel() => _workCancellation?.Cancel();
    [RelayCommand] private void OpenOutput() { if (HasOutput) ShareX.HelpersLib.FileHelpers.OpenFolderWithFile(OutputFilePath); }

    public void Dispose()
    {
        if (_disposed) return;
        SaveEdit();
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _screenPlayer.PositionChanged -= UpdatePlaybackPosition;
        _screenPlayer.IsPlayingChanged -= UpdatePlaybackState;
        _screenPlayer.PlaybackFailed -= HandlePlaybackFailure;
        _screenPlayer.Dispose();
        _facePlayer.Dispose();
    }
}
