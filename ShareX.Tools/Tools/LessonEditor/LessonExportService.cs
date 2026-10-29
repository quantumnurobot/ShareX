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

using ShareX.ScreenRecordingLib;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ShareX.Tools;

/// <summary>Runs lesson exports through FFmpeg. Output is published only after FFmpeg succeeds.</summary>
internal sealed class LessonExportService(string ffmpegPath)
{
    public string FFmpegPath { get; set; } = ffmpegPath;

    public async Task ExportAsync(LessonSession session, string manifestPath, LessonEdit edit, LessonExportKind kind, string output,
        IProgress<double> progress, CancellationToken token, double? previewStart = null, double previewLength = 10)
    {
        string fullOutput = Path.GetFullPath(output);
        foreach (LessonTrack track in session.Tracks)
        {
            if (string.Equals(LessonFiles.Resolve(manifestPath, track), fullOutput, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a different file: exporting cannot overwrite one of the lesson's recorded tracks.");
        }

        string extension = LessonExport.Extension(kind);
        string temporary = Path.Combine(Path.GetDirectoryName(fullOutput)!, $".sharex-lesson-{Guid.NewGuid():N}{extension}");
        string[] arguments = LessonExport.BuildArguments(session, manifestPath, edit, kind, temporary, previewStart, previewLength);
        double duration = previewStart.HasValue
            ? Math.Min(previewLength, edit.EditedDuration(session.DurationSeconds))
            : edit.EditedDuration(session.DurationSeconds);
        try
        {
            await RunAsync(arguments, Math.Max(0.1, duration), progress, token);
            token.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new InvalidOperationException("FFmpeg finished without writing the export.");
            File.Move(temporary, fullOutput, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task RunAsync(IEnumerable<string> arguments, double duration, IProgress<double> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using Process process = new();
        process.StartInfo = new ProcessStartInfo(FFmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { "-hide_banner", "-nostdin" }.Concat(arguments))
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        using CancellationTokenRegistration registration = token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        StringBuilder log = new();
        async Task ReadErrorsAsync()
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                log.AppendLine(line);
                if (log.Length > 32768) log.Remove(0, log.Length - 32768);
            }
        }

        async Task ReadOutputAsync()
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out long microseconds))
                {
                    progress.Report(Math.Clamp(microseconds / 1000000d / duration * 100, 0, 100));
                }
            }
        }

        await Task.WhenAll(ReadErrorsAsync(), ReadOutputAsync(), process.WaitForExitAsync());
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("FFmpeg could not export the lesson: " + log.ToString().Trim());
    }
}
