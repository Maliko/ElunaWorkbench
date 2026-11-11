using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using ElunaWorkbench.Core.Lua;

namespace ElunaWorkbench.Editor;

/// <summary>Draws wavy underlines for diagnostics and highlights references of the symbol under the caret.</summary>
public sealed class DiagnosticRenderer : IBackgroundRenderer
{
    private readonly TextDocument _document;
    private List<(TextAnchor Start, TextAnchor End, DiagnosticSeverity Severity)> _markers = new();
    private List<(int Start, int Length)> _highlights = new();

    public DiagnosticRenderer(TextDocument document)
    {
        _document = document;
    }

    public KnownLayer Layer => KnownLayer.Selection;

    public void SetDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var list = new List<(TextAnchor, TextAnchor, DiagnosticSeverity)>();
        var len = _document.TextLength;
        foreach (var d in diagnostics)
        {
            var start = Math.Clamp(d.Start, 0, len);
            var end = Math.Clamp(d.End, start, len);
            if (end == start)
            {
                // Zero length (e.g. at end of file): mark the previous character.
                if (start > 0) start--;
                else if (end < len) end++;
            }
            var a = _document.CreateAnchor(start);
            var b = _document.CreateAnchor(end);
            a.MovementType = AnchorMovementType.BeforeInsertion;
            b.MovementType = AnchorMovementType.AfterInsertion;
            list.Add((a, b, d.Severity));
        }
        _markers = list;
    }

    public void SetHighlights(List<(int Start, int Length)> highlights) => _highlights = highlights;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid) return;
        var visual = textView.VisualLines;
        if (visual.Count == 0) return;
        var viewStart = visual[0].FirstDocumentLine.Offset;
        var viewEnd = visual[^1].LastDocumentLine.EndOffset;

        var hlBrush = new SolidColorBrush(Color.FromArgb(60, 120, 160, 220));
        foreach (var (start, length) in _highlights)
        {
            if (start + length < viewStart || start > viewEnd || start + length > _document.TextLength) continue;
            var seg = new TextSegment { StartOffset = start, Length = length };
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
            {
                drawingContext.FillRectangle(hlBrush, r);
            }
        }

        foreach (var (a, b, severity) in _markers)
        {
            if (a.IsDeleted || b.IsDeleted) continue;
            var start = a.Offset;
            var end = b.Offset;
            if (end < viewStart || start > viewEnd || end <= start) continue;
            var color = severity switch
            {
                DiagnosticSeverity.Error => Color.FromRgb(244, 71, 71),
                DiagnosticSeverity.Warning => Color.FromRgb(230, 190, 60),
                DiagnosticSeverity.Info => Color.FromRgb(80, 160, 255),
                _ => Color.FromArgb(160, 160, 160, 160),
            };
            var pen = new Pen(new SolidColorBrush(color), severity == DiagnosticSeverity.Hint ? 1 : 1.2);
            var seg = new TextSegment { StartOffset = start, EndOffset = end };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
            {
                if (severity == DiagnosticSeverity.Hint)
                {
                    // dotted line for hints
                    var y = rect.Bottom - 1;
                    for (var x = rect.Left; x < rect.Right; x += 4)
                    {
                        drawingContext.DrawLine(pen, new Point(x, y), new Point(Math.Min(x + 2, rect.Right), y));
                    }
                    continue;
                }
                DrawWave(drawingContext, pen, rect);
            }
        }
    }

    private static void DrawWave(DrawingContext dc, IPen pen, Rect rect)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var y = rect.Bottom - 2;
            var x = rect.Left;
            ctx.BeginFigure(new Point(x, y), false);
            var up = true;
            while (x < rect.Right)
            {
                x += 2;
                ctx.LineTo(new Point(Math.Min(x, rect.Right), up ? y - 2 : y));
                up = !up;
            }
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, geometry);
    }
}
