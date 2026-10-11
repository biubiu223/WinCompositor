using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Compositor.Core.Editing;
using Compositor.Core.Geometry;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.App.Drawing;

/// <summary>The Gradient, Shape and Type tools on the canvas: their pointer handling and what they draw over it.</summary>
public sealed partial class CanvasView
{
    private int _textAnchor;
    private TextInputSink? _textInput;

    /// <summary>The panel over the canvas that holds the (nearly invisible) box typing goes into.</summary>
    public Canvas? TextInputHost
    {
        get => _textInput?.Host;
        set => _textInput = value is null ? null : new TextInputSink(value, this);
    }

    /// <summary>Whether <paramref name="element"/> is where the Type tool's typing goes.</summary>
    public bool IsTextInput(object? element) => _textInput is not null && ReferenceEquals(element, _textInput.Box);

    // MARK: Gradient

    private const double HandleReach = 9;

    private void GradientDown(PointD view, PointD doc)
    {
        var session = _session!;
        // A press on an end of the pending line moves that end.
        if (session.GradientEdit is { HasLine: true } edit && session.Document is { } document)
        {
            var start = session.Viewport.ViewPoint(edit.Start, document.Size);
            var end = session.Viewport.ViewPoint(edit.End, document.Size);
            if (end.DistanceTo(view) <= HandleReach) { StartDrag(Drag.GradientEnd, view); return; }
            if (start.DistanceTo(view) <= HandleReach) { StartDrag(Drag.GradientStart, view); return; }
        }
        session.BeginGradient(doc);
        if (session.GradientEdit is not null) StartDrag(Drag.GradientEnd, view);
    }

    /// <summary>Shift keeps the line at a multiple of 45°.</summary>
    private static PointD Snap45(PointD from, PointD to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4), length = Math.Sqrt(dx * dx + dy * dy);
        return new PointD(from.X + Math.Cos(angle) * length, from.Y + Math.Sin(angle) * length);
    }

    private void ToolDragMove(Drag drag, PointD view, PointD doc)
    {
        var session = _session!;
        switch (drag)
        {
            case Drag.GradientEnd when session.GradientEdit is { } edit:
                session.MoveGradient(end: Shift ? Snap45(edit.Start, doc) : doc);
                break;
            case Drag.GradientStart when session.GradientEdit is { } edit:
                session.MoveGradient(start: Shift ? Snap45(edit.End, doc) : doc);
                break;
            case Drag.Shape:
                session.DragShape(doc, Shift, Alt);
                break;
            case Drag.TypeBox:
                InvalidateVisual();
                break;
            case Drag.TypeSelect when session.TextDraft is { } draft:
                _textInput?.Select(_textAnchor, TextIndexAt(draft, doc));
                break;
        }
    }

    private void ToolDragEnd(Drag drag, PointD view, PointD doc)
    {
        var session = _session!;
        switch (drag)
        {
            case Drag.GradientEnd:
            case Drag.GradientStart:
                session.EndGradientDrag();
                break;
            case Drag.Shape:
                session.FinishShape();
                break;
            case Drag.TypeBox:
            {
                // A drag makes paragraph text in that box; a click makes point text at the pointer.
                if (view.DistanceTo(_dragStartView) > 4)
                {
                    var rect = RectD.FromLTRB(Math.Min(_dragStartDocument.X, doc.X), Math.Min(_dragStartDocument.Y, doc.Y),
                                              Math.Max(_dragStartDocument.X, doc.X), Math.Max(_dragStartDocument.Y, doc.Y));
                    session.BeginTextBox(rect.Integral());
                }
                else session.BeginText(_dragStartDocument);
                _textInput?.Activate();
                break;
            }
        }
    }

    // MARK: Type

    /// <summary>The text being typed and where it shows: its layout box mapped onto the document.</summary>
    private (TextDraft Draft, Affine LayoutToDocument)? TextGeometry()
    {
        if (_session?.TextDraft is not { } draft || _session.Document is not { } document) return null;
        var layer = draft.LayerId is { } id ? document.Find(id) : null;
        try
        {
            var layout = draft.Layout;
            var placement = draft.Placement(layer);
            return (draft, placement.PixelToDocument(Math.Ceiling(layout.Width), Math.Ceiling(layout.Height)));
        }
        catch { return null; }
    }

    private int TextIndexAt(TextDraft draft, PointD doc)
    {
        if (TextGeometry() is not { } geometry || !geometry.LayoutToDocument.IsInvertible) return 0;
        return draft.Layout.IndexAt(geometry.LayoutToDocument.Invert().Apply(doc));
    }

    private bool InsideTextDraft(PointD view)
    {
        if (TextGeometry() is not { } geometry || _session?.Document is not { } document) return false;
        var doc = ToDocument(view);
        if (!geometry.LayoutToDocument.IsInvertible) return false;
        var local = geometry.LayoutToDocument.Invert().Apply(doc);
        var layout = geometry.Draft.Layout;
        return local.X >= -4 && local.Y >= -4 && local.X <= layout.Width + 4 && local.Y <= layout.Height + 4;
    }

    private void TypeDown(PointD view, PointD doc)
    {
        var session = _session!;
        if (session.TextDraft is { } draft)
        {
            if (InsideTextDraft(view))
            {
                // Place the caret (Shift extends the selection), then drag to select.
                int index = TextIndexAt(draft, doc);
                if (Shift) _textInput?.Select(draft.SelectionStart == _textAnchor ? _textAnchor : draft.SelectionStart, index);
                else { _textAnchor = index; _textInput?.Select(index, index); }
                StartDrag(Drag.TypeSelect, view);
                _textInput?.Activate();
                return;
            }
            // A click outside applies the text, as Photoshop's does.
            if (!session.CommitText()) return;
            return;
        }
        StartDrag(Drag.TypeBox, view);
    }

    private bool TypeDoubleClick(PointD view)
    {
        if (_session?.TextDraft is not { } draft || !InsideTextDraft(view)) return false;
        // A word: letters and digits either side of the pointer.
        var content = draft.Style.Content;
        int index = Math.Clamp(TextIndexAt(draft, ToDocument(view)), 0, content.Length);
        int start = index, end = index;
        static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        while (start > 0 && Word(content[start - 1])) start--;
        while (end < content.Length && Word(content[end])) end++;
        _textAnchor = start;
        _textInput?.Select(start, end);
        return true;
    }

    internal void SyncTextInput()
    {
        if (_textInput is null) return;
        if (_session?.TextDraft is not null) _textInput.Activate();
        else _textInput.Deactivate();
    }

    // MARK: Overlays

    private void DrawToolOverlays(SKCanvas canvas, EditorSession session, CanvasDocument document)
    {
        var viewport = session.Viewport;
        PointD V(PointD p) => viewport.ViewPoint(p, document.Size);

        // The pending gradient's line and its two ends.
        if (session.GradientEdit is { HasLine: true } gradient)
        {
            var a = V(gradient.Start).ToSK();
            var b = V(gradient.End).ToSK();
            using var dark = new SKPaint { Color = new SKColor(0, 0, 0, 160), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var light = new SKPaint { Color = SKColors.White, StrokeWidth = 1.2f, IsAntialias = true, Style = SKPaintStyle.Stroke };
            canvas.DrawLine(a, b, dark);
            canvas.DrawLine(a, b, light);
            using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var edge = new SKPaint { Color = Accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
            foreach (var p in new[] { a, b })
            {
                canvas.DrawCircle(p, 5, fill);
                canvas.DrawCircle(p, 5, edge);
            }
        }

        // The shape being dragged out, in the color it will be.
        if (session.ShapeDraft is { } shape)
        {
            var color = session.ForegroundColor.ToSK(160);
            var outline = new SKPaint { Color = Accent, StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using (outline)
            {
                if (shape.Kind == ShapeKind.Line)
                {
                    var from = V(shape.Anchor).ToSK();
                    var to = V(shape.End ?? shape.Anchor).ToSK();
                    using var line = new SKPaint { Color = color, StrokeWidth = (float)Math.Max(1, session.ShapeLineWidth * viewport.PointsPerPixel), StrokeCap = SKStrokeCap.Round, IsAntialias = true, Style = SKPaintStyle.Stroke };
                    canvas.DrawLine(from, to, line);
                }
                else if (shape.Rect.Width > 0 && shape.Rect.Height > 0)
                {
                    var rect = RectD.FromLTRB(V(shape.Rect.Origin).X, V(shape.Rect.Origin).Y,
                        V(new PointD(shape.Rect.Right, shape.Rect.Bottom)).X, V(new PointD(shape.Rect.Right, shape.Rect.Bottom)).Y).ToSK();
                    using var fill = new SKPaint { Color = color, IsAntialias = true };
                    if (shape.Kind == ShapeKind.Ellipse) { canvas.DrawOval(rect, fill); canvas.DrawOval(rect, outline); }
                    else
                    {
                        float radius = (float)(Math.Min(shape.CornerRadius, Math.Min(shape.Rect.Width, shape.Rect.Height) / 2) * viewport.PointsPerPixel);
                        canvas.DrawRoundRect(rect, radius, radius, fill);
                        canvas.DrawRoundRect(rect, radius, radius, outline);
                    }
                }
            }
        }

        // A text box being dragged out.
        if (_drag == Drag.TypeBox && _hover is { } hover && hover.DistanceTo(_dragStartView) > 4)
        {
            using var box = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0) };
            canvas.DrawRect(RectD.FromLTRB(Math.Min(hover.X, _dragStartView.X), Math.Min(hover.Y, _dragStartView.Y),
                Math.Max(hover.X, _dragStartView.X), Math.Max(hover.Y, _dragStartView.Y)).ToSK(), box);
        }

        DrawTextDraft(canvas, session, document);

        // Clone Stamp: a crosshair where the brush copies from.
        if (session.Tool == Tool.CloneStamp && _hover is { } pointer && session.CloneSamplePoint(ToDocument(pointer)) is { } source)
        {
            var at = V(source).ToSK();
            using var dark = new SKPaint { Color = new SKColor(0, 0, 0, 170), StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
            using var light = new SKPaint { Color = SKColors.White, StrokeWidth = 1.2f, IsAntialias = true, Style = SKPaintStyle.Stroke };
            foreach (var paint in new[] { dark, light })
            {
                canvas.DrawLine(at.X - 8, at.Y, at.X + 8, at.Y, paint);
                canvas.DrawLine(at.X, at.Y - 8, at.X, at.Y + 8, paint);
            }
        }
    }

    /// <summary>Text being typed: new text drawn as it will be; its box, the selection and the caret over it.</summary>
    private void DrawTextDraft(SKCanvas canvas, EditorSession session, CanvasDocument document)
    {
        if (TextGeometry() is not { } geometry) return;
        var (draft, toDocument) = geometry;
        var toView = toDocument.Concat(session.Viewport.DocumentToView(document.Size));
        var layout = draft.Layout;
        canvas.Save();
        canvas.Concat(toView.ToSK());
        if (draft.LayerId is null)
        {
            try
            {
                var image = draft.Image.Image;
                using var paint = new SKPaint();
                canvas.DrawImage(image, new SKRect(0, 0, image.Width, image.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            }
            catch { /* too large to show */ }
        }
        float hairline = (float)(1 / Math.Max(1e-6, Math.Sqrt(Math.Abs(toView.Determinant))));
        using (var selection = new SKPaint { Color = new SKColor(47, 123, 246, 90) })
            foreach (var r in layout.SelectionRects(draft.SelectionStart, draft.SelectionStart + draft.SelectionLength))
                canvas.DrawRect(r.ToSK(), selection);
        using (var box = new SKPaint { Color = new SKColor(255, 255, 255, 150), Style = SKPaintStyle.Stroke, StrokeWidth = hairline, PathEffect = SKPathEffect.CreateDash(new[] { 4 * hairline, 3 * hairline }, 0) })
            canvas.DrawRect(new SKRect(0, 0, (float)layout.Width, (float)layout.Height), box);
        if (draft.SelectionLength == 0 && IsFocusedForTyping)
        {
            var (x, top, height) = layout.Caret(draft.SelectionStart);
            using var caret = new SKPaint { Color = draft.Style.ColorAt(Math.Max(0, draft.SelectionStart - 1)).Luminance > 0.5 ? SKColors.Black : SKColors.White, StrokeWidth = 2 * hairline };
            using var halo = new SKPaint { Color = new SKColor(128, 128, 128, 160), StrokeWidth = 4 * hairline };
            canvas.DrawLine((float)x, (float)top, (float)x, (float)(top + height), halo);
            canvas.DrawLine((float)x, (float)top, (float)x, (float)(top + height), caret);
        }
        canvas.Restore();
    }

    private bool IsFocusedForTyping => _textInput?.Box.IsKeyboardFocused == true;

    /// <summary>Where the caret is on screen, in this view's points (for placing the typing box at it).</summary>
    internal (Point Position, double Height)? CaretOnScreen()
    {
        if (TextGeometry() is not { } geometry || _session?.Document is not { } document) return null;
        var (draft, toDocument) = geometry;
        var (x, top, height) = draft.Layout.Caret(draft.SelectionStart + draft.SelectionLength);
        var toView = toDocument.Concat(_session.Viewport.DocumentToView(document.Size));
        var a = toView.Apply(x, top);
        var b = toView.Apply(x, top + height);
        return (new Point(a.X, a.Y), Math.Max(4, a.DistanceTo(b)));
    }
}
