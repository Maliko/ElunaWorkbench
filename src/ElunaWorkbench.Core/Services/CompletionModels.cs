using ElunaWorkbench.Core.Api;

namespace ElunaWorkbench.Core.Services;

public enum CompletionKind
{
    Keyword,
    Snippet,
    Local,
    Parameter,
    Global,
    Function,
    Method,
    Field,
    Library,
    Class,
    Event,
}

public sealed class CompletionItem
{
    public required string Label { get; init; }
    public required CompletionKind Kind { get; init; }

    /// <summary>Short info shown next to the label (signature, type).</summary>
    public string Detail { get; init; } = "";

    /// <summary>Longer documentation shown in the tooltip.</summary>
    public string Documentation { get; init; } = "";

    /// <summary>Text to insert. May contain "$0" for the caret position (snippets).</summary>
    public string? InsertText { get; init; }

    /// <summary>Lower = shown first.</summary>
    public int Priority { get; init; } = 50;

    /// <summary>True if this entry is not available on the project's target core.</summary>
    public bool Unavailable { get; init; }

    public ApiFunction? Function { get; init; }

    public string TextToInsert => InsertText ?? Label;

    public override string ToString() => $"{Kind} {Label}";
}

public sealed class CompletionResult
{
    public static readonly CompletionResult Empty = new() { ReplaceStart = 0, ReplaceLength = 0 };

    /// <summary>Offset of the typed prefix that will be replaced.</summary>
    public required int ReplaceStart { get; init; }
    public required int ReplaceLength { get; init; }
    public string Prefix { get; init; } = "";
    public List<CompletionItem> Items { get; } = new();

    /// <summary>True when the completion is for members after '.' or ':'.</summary>
    public bool IsMemberCompletion { get; init; }
}

public sealed class SignatureHelp
{
    public required ApiFunction Function { get; init; }
    public required int ActiveParameter { get; init; }

    /// <summary>Extra info, e.g. the list of event ids for Register*Event.</summary>
    public string? Extra { get; init; }
}

public sealed class HoverInfo
{
    public required string Title { get; init; }
    public string Body { get; init; } = "";
    public int Start { get; init; }
    public int Length { get; init; }
}

public sealed record DefinitionLocation(string FilePath, int Offset, int Length);
