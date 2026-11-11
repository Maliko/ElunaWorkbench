using ElunaWorkbench.Core.Api;
using ElunaWorkbench.Core.Lua;

namespace ElunaWorkbench.Core.Analysis;

public enum SymbolKind
{
    Local,
    Parameter,
    LocalFunction,
    LoopVariable,
    Global,
    GlobalFunction,
    Field,
    Label,
}

public sealed class FieldInfo
{
    public required string Name { get; init; }
    public string? Type { get; set; }
    public FunctionExpr? Function { get; set; }
    public int Offset { get; init; }
    public string? FilePath { get; set; }
    public Dictionary<string, FieldInfo> Fields { get; } = new(StringComparer.Ordinal);
}

public sealed class Symbol
{
    public required string Name { get; init; }
    public required SymbolKind Kind { get; init; }

    /// <summary>Declaration identifier (start offset of the name).</summary>
    public required int DeclarationStart { get; init; }

    public int DeclarationLength => Name.Length;

    /// <summary>Offset from which on the symbol is visible.</summary>
    public int VisibleFrom { get; set; }

    public Scope? Scope { get; set; }

    /// <summary>Inferred type: Eluna class name, "string", "number", "table", "function", ... or null.</summary>
    public string? Type { get; set; }

    /// <summary>True when the type was only guessed from the variable name.</summary>
    public bool TypeFromName { get; set; }

    /// <summary>The function this symbol is bound to (local function / local x = function ...).</summary>
    public FunctionExpr? Function { get; set; }

    /// <summary>Known fields if the symbol holds a table.</summary>
    public Dictionary<string, FieldInfo> Fields { get; } = new(StringComparer.Ordinal);

    public List<int> References { get; } = new();
    public int ReadCount { get; set; }
    public int WriteCount { get; set; }

    /// <summary>Additional explanation (e.g. "Handler für PLAYER_EVENT_ON_LOGIN").</summary>
    public string? Detail { get; set; }

    public bool IsLocal => Kind is SymbolKind.Local or SymbolKind.Parameter or SymbolKind.LocalFunction or SymbolKind.LoopVariable;

    public override string ToString() => $"{Kind} {Name}: {Type ?? "?"}";
}

public sealed class Scope
{
    public Scope(Scope? parent, int start, int end, FunctionExpr? function = null)
    {
        Parent = parent;
        Start = start;
        End = end;
        Function = function ?? parent?.Function;
        parent?.Children.Add(this);
    }

    public Scope? Parent { get; }
    public int Start { get; }
    public int End { get; set; }
    public FunctionExpr? Function { get; }
    public List<Scope> Children { get; } = new();
    public List<Symbol> Symbols { get; } = new();

    public Symbol Declare(Symbol symbol)
    {
        symbol.Scope = this;
        Symbols.Add(symbol);
        return symbol;
    }

    /// <summary>Finds the innermost scope containing the offset.</summary>
    public Scope FindScope(int offset)
    {
        foreach (var child in Children)
        {
            if (offset >= child.Start && offset <= child.End) return child.FindScope(offset);
        }
        return this;
    }

    /// <summary>Looks up a local symbol visible at the offset.</summary>
    public Symbol? Lookup(string name, int offset)
    {
        for (var s = this; s != null; s = s.Parent)
        {
            for (var i = s.Symbols.Count - 1; i >= 0; i--)
            {
                var sym = s.Symbols[i];
                if (sym.Name == name && sym.VisibleFrom <= offset) return sym;
            }
        }
        return null;
    }

    /// <summary>All symbols visible at the offset (innermost first, shadowed ones removed).</summary>
    public IEnumerable<Symbol> VisibleSymbols(int offset)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var s = this; s != null; s = s.Parent)
        {
            for (var i = s.Symbols.Count - 1; i >= 0; i--)
            {
                var sym = s.Symbols[i];
                if (sym.VisibleFrom <= offset && sym.Kind != SymbolKind.Label && seen.Add(sym.Name)) yield return sym;
            }
        }
    }
}

/// <summary>A global defined in a project file.</summary>
public sealed class GlobalDefinition
{
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public required int Offset { get; init; }
    public string? Type { get; set; }
    public FunctionExpr? Function { get; set; }
    public Dictionary<string, FieldInfo> Fields { get; } = new(StringComparer.Ordinal);
    public bool IsFunction => Function != null || Type == "function";
}

/// <summary>Information about how a function is used as an event handler.</summary>
public sealed class HandlerInfo
{
    public required string Description { get; init; }
    public List<string?> ParameterTypes { get; } = new();
    public ApiEvent? Event { get; init; }
}

/// <summary>Registration of AIO handlers: AIO.AddHandlers("Name", table) / AIO.RegisterEvent("Name", fn).</summary>
public sealed class AioRegistration
{
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public required int Offset { get; init; }

    /// <summary>True if the handlers live on the client (WoW addon) side.</summary>
    public bool IsClient { get; init; }

    public HashSet<string> HandlerNames { get; } = new(StringComparer.Ordinal);

    /// <summary>False when handler names could not be determined statically.</summary>
    public bool HandlersKnown { get; set; } = true;
}
