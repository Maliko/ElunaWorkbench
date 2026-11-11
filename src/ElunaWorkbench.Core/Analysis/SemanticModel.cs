using ElunaWorkbench.Core.Api;
using ElunaWorkbench.Core.Lua;

namespace ElunaWorkbench.Core.Analysis;

/// <summary>Result of analysing one Lua file.</summary>
public sealed class SemanticModel
{
    internal SemanticModel(ParseResult parse, AnalysisContext context, Scope root)
    {
        Parse = parse;
        Context = context;
        RootScope = root;
    }

    public ParseResult Parse { get; }
    public AnalysisContext Context { get; }
    public ApiDatabase Api => Context.Api;
    public Scope RootScope { get; }
    public string Text => Parse.Text;
    public string FilePath => Context.FilePath;

    public bool IsClientFile { get; internal set; }

    public List<Diagnostic> Diagnostics { get; } = new();

    /// <summary>Resolved local symbol by offset of a name occurrence (declaration or reference).</summary>
    internal Dictionary<int, Symbol> SymbolAt { get; } = new();

    public List<Symbol> AllSymbols { get; } = new();

    public Dictionary<FunctionExpr, HandlerInfo> Handlers { get; } = new();

    /// <summary>Globals defined in this file.</summary>
    public Dictionary<string, GlobalDefinition> GlobalDefinitions { get; } = new(StringComparer.Ordinal);

    public List<AioRegistration> AioRegistrations { get; } = new();

    /// <summary>Global names that are read in this file.</summary>
    public HashSet<string> GlobalReads { get; } = new(StringComparer.Ordinal);

    /// <summary>Top level functions for the outline.</summary>
    public List<(string Name, int Offset)> Outline { get; } = new();

    public Scope ScopeAt(int offset) => RootScope.FindScope(offset);

    public Symbol? FindSymbolAt(int offset)
    {
        foreach (var (start, sym) in SymbolAt)
        {
            if (offset >= start && offset <= start + sym.Name.Length) return sym;
        }
        return null;
    }

    public GlobalDefinition? FindGlobal(string name)
    {
        if (GlobalDefinitions.TryGetValue(name, out var g)) return g;
        return Context.ProjectGlobals(name);
    }

    // -----------------------------------------------------------------------------------------
    // Type evaluation – usable for arbitrary expressions at a given offset (completion, hover).
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the type of an expression evaluated in the scope at <paramref name="offset"/>.
    /// Library tables are returned as "lib:name".
    /// </summary>
    public string? TypeOf(Expr expr, int offset, Func<NameExpr, Symbol?>? resolver = null)
    {
        resolver ??= n => ScopeAt(offset).Lookup(n.Name, offset);
        return TypeEvaluator.Evaluate(this, expr, resolver);
    }

    /// <summary>Parses a standalone expression (e.g. the text in front of a '.' or ':').</summary>
    public static Expr? ParseExpression(string text)
    {
        var result = Parser.Parse("return " + text);
        if (result.Chunk.Body.Statements.FirstOrDefault() is ReturnStmt r && r.Values.Count == 1)
        {
            var expr = r.Values[0];
            Shift(expr, -7);
            return expr;
        }
        return null;
    }

    private static void Shift(Node node, int delta)
    {
        foreach (var n in AstNodes.Descendants(node))
        {
            n.Start += delta;
            n.End += delta;
        }
    }
}

/// <summary>Computes static types of expressions.</summary>
internal static class TypeEvaluator
{
    public static string? Evaluate(SemanticModel model, Expr expr, Func<NameExpr, Symbol?> resolve, int depth = 0)
    {
        if (depth > 30) return null;
        var api = model.Api;
        switch (expr)
        {
            case LiteralExpr lit:
                return lit.Kind switch
                {
                    LiteralKind.String => "string",
                    LiteralKind.Number => "number",
                    LiteralKind.True or LiteralKind.False => "boolean",
                    LiteralKind.Nil => "nil",
                    _ => null,
                };
            case TableExpr:
                return "table";
            case FunctionExpr:
                return "function";
            case ParenExpr p:
                return Evaluate(model, p.Inner, resolve, depth + 1);
            case NameExpr name:
            {
                var sym = resolve(name);
                if (sym != null) return sym.Type;
                var g = model.FindGlobal(name.Name);
                if (g?.Type != null) return g.Type;
                if (api.FindLibrary(name.Name) != null) return "lib:" + name.Name;
                if (api.FindGlobal(name.Name) != null) return "function";
                var field = api.FindGlobalField(name.Name);
                if (field != null) return field.Type;
                return g != null ? (g.IsFunction ? "function" : null) : null;
            }
            case MemberExpr member:
            {
                var objType = Evaluate(model, member.Object, resolve, depth + 1);
                if (objType != null && objType.StartsWith("lib:"))
                {
                    var lib = api.FindLibrary(objType[4..]);
                    if (lib == null) return null;
                    if (lib.Functions.Any(f => f.Name == member.Member.Name)) return "function";
                    return lib.Fields.FirstOrDefault(f => f.Name == member.Member.Name)?.Type;
                }
                var fields = FieldsOf(model, member.Object, resolve, depth);
                if (fields != null && fields.TryGetValue(member.Member.Name, out var fi))
                {
                    return fi.Type ?? (fi.Function != null ? "function" : null);
                }
                return null;
            }
            case CallExpr call:
            {
                var fn = ResolveCallee(model, call.Callee, resolve, depth);
                if (fn != null)
                {
                    // require("AIO") returns the AIO library
                    if (fn.Name == "require" && call.Args.FirstOrDefault() is LiteralExpr { Kind: LiteralKind.String } s && api.FindLibrary(s.StringValue ?? "") != null)
                    {
                        return "lib:" + s.StringValue;
                    }
                    return ReturnTypeOf(api, fn);
                }
                return null;
            }
            case MethodCallExpr mc:
            {
                var objType = Evaluate(model, mc.Object, resolve, depth + 1);
                var m = FindMethod(api, objType, mc.Method.Name);
                return m != null ? ReturnTypeOf(api, m) : null;
            }
            case BinaryExpr bin:
                switch (bin.Operator)
                {
                    case "..":
                        return "string";
                    case "+":
                    case "-":
                    case "*":
                    case "/":
                    case "//":
                    case "%":
                    case "^":
                    case "&":
                    case "|":
                    case "~":
                    case "<<":
                    case ">>":
                        return "number";
                    case "==":
                    case "~=":
                    case "<":
                    case ">":
                    case "<=":
                    case ">=":
                        return "boolean";
                    case "or":
                    {
                        var l = Evaluate(model, bin.Left, resolve, depth + 1);
                        return l is null or "nil" or "boolean" ? Evaluate(model, bin.Right, resolve, depth + 1) ?? l : l;
                    }
                    case "and":
                        return Evaluate(model, bin.Right, resolve, depth + 1);
                }
                return null;
            case UnaryExpr un:
                return un.Operator switch
                {
                    "not" => "boolean",
                    "#" or "-" or "~" => "number",
                    _ => null,
                };
        }
        return null;
    }

    public static string? ReturnTypeOf(ApiDatabase api, ApiFunction fn)
    {
        var r = fn.ReturnType;
        if (r == null) return null;
        var t = api.ToLuaType(r);
        return t == "any" ? null : t;
    }

    /// <summary>Finds a method for obj:name() for Eluna classes, AIO messages and strings.</summary>
    public static ApiFunction? FindMethod(ApiDatabase api, string? objType, string name)
    {
        if (objType == null) return null;
        if (objType == "string")
        {
            return api.FindLibrary("string")?.Functions.FirstOrDefault(f => f.Name == name);
        }
        return api.FindMethod(objType, name);
    }

    public static ApiFunction? ResolveCallee(SemanticModel model, Expr callee, Func<NameExpr, Symbol?> resolve, int depth)
    {
        var api = model.Api;
        if (callee is NameExpr n)
        {
            if (resolve(n) != null) return null; // shadowed by a local
            if (model.FindGlobal(n.Name) is { } g && api.FindGlobal(n.Name) == null) return null;
            return api.FindGlobal(n.Name);
        }
        if (callee is MemberExpr m)
        {
            var objType = Evaluate(model, m.Object, resolve, depth + 1);
            if (objType != null && objType.StartsWith("lib:"))
            {
                return api.FindLibrary(objType[4..])?.Functions.FirstOrDefault(f => f.Name == m.Member.Name);
            }
        }
        return null;
    }

    public static Dictionary<string, FieldInfo>? FieldsOf(SemanticModel model, Expr obj, Func<NameExpr, Symbol?> resolve, int depth)
    {
        switch (obj)
        {
            case NameExpr n:
            {
                var sym = resolve(n);
                if (sym != null) return sym.Fields;
                return model.FindGlobal(n.Name)?.Fields;
            }
            case MemberExpr m:
            {
                var parent = FieldsOf(model, m.Object, resolve, depth + 1);
                if (parent != null && parent.TryGetValue(m.Member.Name, out var fi)) return fi.Fields;
                return null;
            }
            case ParenExpr p:
                return FieldsOf(model, p.Inner, resolve, depth + 1);
        }
        return null;
    }
}
