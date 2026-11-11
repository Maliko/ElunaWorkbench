namespace ElunaWorkbench.Core.Lua;

public enum DiagnosticSeverity
{
    Hint,
    Info,
    Warning,
    Error,
}

/// <summary>A single text replacement.</summary>
public sealed record TextEdit(int Start, int Length, string NewText);

/// <summary>A quick fix ("Korrektur") that can be applied to resolve a diagnostic.</summary>
public sealed class CodeFix
{
    public CodeFix(string title, params TextEdit[] edits)
    {
        Title = title;
        Edits = edits.ToList();
    }

    public string Title { get; }
    public List<TextEdit> Edits { get; }

    /// <summary>Applies the fix to a text and returns the new text.</summary>
    public string Apply(string text)
    {
        foreach (var e in Edits.OrderByDescending(e => e.Start))
        {
            text = text.Remove(e.Start, e.Length).Insert(e.Start, e.NewText);
        }
        return text;
    }

    public override string ToString() => Title;
}

public sealed class Diagnostic
{
    public Diagnostic(DiagnosticSeverity severity, string code, string message, int start, int length, int line = 0, int column = 0)
    {
        Severity = severity;
        Code = code;
        Message = message;
        Start = start;
        Length = Math.Max(0, length);
        Line = line;
        Column = column;
    }

    public DiagnosticSeverity Severity { get; set; }
    public string Code { get; }
    public string Message { get; }
    public int Start { get; }
    public int Length { get; }
    public int End => Start + Length;

    /// <summary>1-based line.</summary>
    public int Line { get; set; }

    /// <summary>1-based column.</summary>
    public int Column { get; set; }

    public List<CodeFix> Fixes { get; } = new();

    /// <summary>File the diagnostic belongs to (set by the workspace).</summary>
    public string? FilePath { get; set; }

    public override string ToString() => $"{Severity} {Code} ({Line}:{Column}): {Message}";
}

/// <summary>Maps offsets to line/column positions.</summary>
public sealed class LineIndex
{
    private readonly List<int> _lineStarts = new() { 0 };

    public LineIndex(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') _lineStarts.Add(i + 1);
            else if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                _lineStarts.Add(i + 1);
            }
        }
        Length = text.Length;
    }

    public int Length { get; }
    public int LineCount => _lineStarts.Count;

    /// <summary>Returns 1-based (line, column).</summary>
    public (int Line, int Column) GetPosition(int offset)
    {
        offset = Math.Clamp(offset, 0, Length);
        var idx = _lineStarts.BinarySearch(offset);
        if (idx < 0) idx = ~idx - 1;
        return (idx + 1, offset - _lineStarts[idx] + 1);
    }

    public int GetLineStart(int line) => _lineStarts[Math.Clamp(line - 1, 0, _lineStarts.Count - 1)];

    public int GetOffset(int line, int column) => Math.Min(Length, GetLineStart(line) + Math.Max(0, column - 1));
}
