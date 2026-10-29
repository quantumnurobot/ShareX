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

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using ShareX.AvaloniaUI.Theming;
using ShareX.ScreenRecordingLib;

namespace ShareX.Tools;

public partial class LessonEditorWindow : Window
{
    private readonly LessonEditorViewModel _viewModel;

    public LessonEditorWindow() : this("ffmpeg.exe") { }

    public LessonEditorWindow(string ffmpegPath, string? lessonFilePath = null, Action? playNotificationSound = null,
        Func<string?>? resolveFFmpegPath = null)
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = new(ffmpegPath, this.FindControl<WindowsMediaPlayer>("ScreenPlayer")!, this.FindControl<WindowsMediaPlayer>("FacePlayer")!,
            playNotificationSound, resolveFFmpegPath);
        DataContext = _viewModel;
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        _viewModel.SelectLessonRequested = SelectLessonAsync;
        _viewModel.SelectOutputRequested = SelectOutputAsync;
        _viewModel.OpenPreviewRequested = path => new VideoPlayerWindow(path).Show(this);
        _viewModel.ShowErrorRequested = message => ShareX.AvaloniaUI.MessageBox.Show(
            this, message, "Lesson Editor", ShareX.AvaloniaUI.MessageBoxButtons.OK, ShareX.AvaloniaUI.MessageBoxIcon.Error);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Opened += async (_, _) =>
        {
            if (!string.IsNullOrEmpty(lessonFilePath)) await _viewModel.LoadLessonAsync(lessonFilePath);
        };
        Closed += (_, _) => _viewModel.Dispose();
    }

    private async Task<string?> SelectLessonAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open lesson",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Lesson recordings") { Patterns = ["*" + LessonFiles.ManifestSuffix] },
                new FilePickerFileType("Lesson screen video") { Patterns = ["*.mp4"] }
            ]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> SelectOutputAsync(string suggestedName, string extension)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(_viewModel.LessonFilePath)!),
            DefaultExtension = extension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(extension == ".m4a" ? "Audio" : "Video") { Patterns = ["*" + extension] }]
        });
        return file?.TryGetLocalPath();
    }

    private void OnRemoveCutClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: LessonCut cut }) _viewModel.RemoveCutCommand.Execute(cut);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _viewModel.CanBrowse && e.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!_viewModel.CanBrowse) return;
        string? path = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().FirstOrDefault()?.TryGetLocalPath();
        if (path != null)
        {
            e.Handled = true;
            await _viewModel.LoadLessonAsync(path);
        }
    }
}
