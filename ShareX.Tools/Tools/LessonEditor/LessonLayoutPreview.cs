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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ShareX.ScreenRecordingLib;
using System.ComponentModel;

namespace ShareX.Tools;

/// <summary>
/// Where the face sits in the blended lesson, drawn with the same geometry as the export.
/// Drag the face to place it anywhere (switches placement to Custom).
/// </summary>
public sealed class LessonLayoutPreview : Control
{
    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<LessonLayoutPreview, IBrush?>(nameof(Accent), Brushes.DodgerBlue);
    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    private static readonly IBrush screenFill = new SolidColorBrush(Color.FromRgb(30, 41, 59));
    private static readonly IPen screenPen = new Pen(new SolidColorBrush(Color.FromRgb(100, 116, 139)), 1);
    private static readonly IBrush faceFill = new SolidColorBrush(Color.FromArgb(220, 59, 130, 246));
    private static readonly Typeface typeface = new(FontFamily.Default);
    private LessonEditorViewModel? _model;
    private bool _dragging;
    private Point _grab;

    static LessonLayoutPreview()
    {
        AffectsRender<LessonLayoutPreview>(AccentProperty);
    }

    public LessonLayoutPreview()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        Unsubscribe();
        _model = DataContext as LessonEditorViewModel;
        if (_model != null)
        {
            _model.PropertyChanged += ModelChanged;
            _model.EditUpdated += InvalidateVisual;
        }
        InvalidateVisual();
    }

    private void Unsubscribe()
    {
        if (_model == null) return;
        _model.PropertyChanged -= ModelChanged;
        _model.EditUpdated -= InvalidateVisual;
        _model = null;
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    /// <summary>The screen area in control coordinates and the scale from recording pixels.</summary>
    private bool TryGetScreen(out Rect screen, out double scale)
    {
        screen = default;
        scale = 0;
        if (_model?.Session is not { ScreenWidth: > 0, ScreenHeight: > 0 } session) return false;
        double available = Math.Max(1, Bounds.Width - 8), availableHeight = Math.Max(1, Bounds.Height - 8);
        scale = Math.Min(available / session.ScreenWidth, availableHeight / session.ScreenHeight);
        double width = session.ScreenWidth * scale, height = session.ScreenHeight * scale;
        screen = new((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        return true;
    }

    /// <summary>The face rectangle in recording pixels, matching LessonExport.</summary>
    private Rect FaceRect(LessonSession session, LessonEdit edit)
    {
        double width = Math.Max(2, (int)Math.Round(session.ScreenWidth * edit.FaceWidthPercent / 100d) & ~1);
        LessonTrack? camera = session.Find(LessonTrackKind.Camera);
        double height = edit.FaceShape == CameraOverlayShape.Circle || camera is not { Width: > 0, Height: > 0 }
            ? width : Math.Round(width * camera.Height / camera.Width / 2) * 2;
        double freeX = session.ScreenWidth - width, freeY = session.ScreenHeight - height, m = edit.FaceMargin;
        (double x, double y) = edit.FacePlacement switch
        {
            LessonFacePlacement.TopLeft => (m, m),
            LessonFacePlacement.TopRight => (freeX - m, m),
            LessonFacePlacement.BottomLeft => (m, freeY - m),
            LessonFacePlacement.Custom => (freeX * edit.FaceX, freeY * edit.FaceY),
            _ => (freeX - m, freeY - m)
        };
        return new(x, y, width, height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!TryGetScreen(out Rect screen, out double scale) || _model?.Session is not { } session) return;
        context.DrawRectangle(screenFill, screenPen, screen, 3, 3);
        DrawLabel(context, "Screen", new Point(screen.X + 8, screen.Y + 6), Brushes.LightGray);

        LessonEdit edit = _model.CurrentEdit();
        if (!_model.HasFaceTrack && session.Find(LessonTrackKind.Camera) == null) return;
        if (!edit.ShowFace)
        {
            DrawLabel(context, "Face hidden", new Point(screen.X + 8, screen.Bottom - 22), Brushes.LightGray);
            return;
        }
        Rect face = FaceRect(session, edit);
        Rect drawn = new(screen.X + face.X * scale, screen.Y + face.Y * scale, face.Width * scale, face.Height * scale);
        IPen outline = new Pen(Accent, 2);
        if (edit.FaceShape == CameraOverlayShape.Circle) context.DrawEllipse(faceFill, outline, drawn);
        else context.DrawRectangle(faceFill, outline, drawn, 2, 2);
        DrawLabel(context, "You", new Point(drawn.Center.X - 12, drawn.Center.Y - 8), Brushes.White);
    }

    private static void DrawLabel(DrawingContext context, string text, Point origin, IBrush brush)
    {
        FormattedText formatted = new(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 12, brush);
        context.DrawText(formatted, origin);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_model is not { CanEdit: true, ShowFace: true, Session: { } session } model || !TryGetScreen(out Rect screen, out double scale)) return;
        Rect face = FaceRect(session, model.CurrentEdit());
        Rect drawn = new(screen.X + face.X * scale, screen.Y + face.Y * scale, face.Width * scale, face.Height * scale);
        Point point = e.GetPosition(this);
        if (!drawn.Contains(point)) return;
        _dragging = true;
        _grab = new Point(point.X - drawn.X, point.Y - drawn.Y);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || _model?.Session is not { } session || !TryGetScreen(out Rect screen, out double scale)) return;
        Rect face = FaceRect(session, _model.CurrentEdit());
        double freeX = (session.ScreenWidth - face.Width) * scale, freeY = (session.ScreenHeight - face.Height) * scale;
        Point point = e.GetPosition(this);
        double left = point.X - _grab.X - screen.X, top = point.Y - _grab.Y - screen.Y;
        _model.MoveFace(freeX > 0 ? left / freeX : 0, freeY > 0 ? top / freeY : 0);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _dragging = false;
        base.OnPointerCaptureLost(e);
    }
}
