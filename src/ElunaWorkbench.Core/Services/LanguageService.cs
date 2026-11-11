using System.Text;
using ElunaWorkbench.Core.Analysis;
using ElunaWorkbench.Core.Api;
using ElunaWorkbench.Core.Lua;

namespace ElunaWorkbench.Core.Services;

/// <summary>
/// Editor features on top of the semantic model: completion, signature help, hover and
/// go-to-definition. All methods take the model of the current text.
/// </summary>
public static class LanguageService
{
    // =========================================================================================
    // Completion
    // =========================================================================================

    public static CompletionResult GetCompletions(SemanticModel model, int offset, bool explicitInvoke = false)
    {
        var text = model.Text;
        offset = Math.Clamp(offset, 0, text.Length);
        if (IsInCommentOrString(model, offset)) return CompletionResult.Empty;

        var prefixStart = offset;
        while (prefixStart > 0 && IsIdentChar(text[prefixStart - 1])) prefixStart--;
        var prefix = text.Substring(prefixStart, offset - prefixStart);
        if (prefix.Length > 0 && char.IsDigit(prefix[0])) return CompletionResult.Empty;

        // Member completion after '.' or ':'
        if (prefixStart > 0 && (text[prefixStart - 1] == ':' || text[prefixStart - 1] == '.') && !(prefixStart > 1 && text[prefixStart - 2] == '.'))
        {
            var sep = text[prefixStart - 1];
            var exprStart = FindExpressionStart(model, prefixStart - 1);
            if (exprStart < 0) return CompletionResult.Empty;
            var exprText = text.Substring(exprStart, prefixStart - 1 - exprStart);
            var result = new CompletionResult { ReplaceStart = prefixStart, ReplaceLength = prefix.Length, Prefix = prefix, IsMemberCompletion = true };
            AddMemberCompletions(model, result, exprText, sep, offset);
            Filter(result, prefix);
            return result;
        }

        // After "function " / "local " a new name is typed – no completion unless invoked explicitly.
        var prevWord = PreviousWord(text, prefixStart);
        if (!explicitInvoke && prevWord is "local" or "function" or "for") return CompletionResult.Empty;

        var general = new CompletionResult { ReplaceStart = prefixStart, ReplaceLength = prefix.Length, Prefix = prefix };

        var call = FindEnclosingCall(model, offset);
        if (call != null) AddArgumentCompletions(model, general, call.Value, offset);

        AddGeneralCompletions(model, general, offset, prevWord);
        Filter(general, prefix);
        return general;
    }

    private static string? PreviousWord(string text, int pos)
    {
        var i = pos - 1;
        while (i >= 0 && (text[i] == ' ' || text[i] == '\t')) i--;
        var end = i + 1;
        while (i >= 0 && IsIdentChar(text[i])) i--;
        return end > i + 1 ? text.Substring(i + 1, end - i - 1) : null;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    public static bool IsInCommentOrString(SemanticModel model, int offset)
    {
        foreach (var t in model.Parse.Tokens)
        {
            if (t.Start >= offset) break;
            if (t.Kind is not (TokenKind.Comment or TokenKind.String)) continue;
            if (offset > t.Start && offset < t.End) return true;
            // Line comments and unterminated strings include the caret at their end.
            if (offset == t.End)
            {
                if (t.Kind == TokenKind.Comment && !t.Text.StartsWith("--[")) return true;
                if (t.Kind == TokenKind.String && (t.Length < 2 || t.Text[^1] != t.Text[0] && !t.Text.EndsWith("]"))) return true;
            }
        }
        return false;
    }

    private static void Filter(CompletionResult result, string prefix)
    {
        var filtered = result.Items
            .Where(i => TextUtil.FuzzyMatch(i.Label, prefix))
            .GroupBy(i => (i.Label, i.Kind == CompletionKind.Snippet))
            .Select(g => g.OrderBy(i => i.Priority).First())
            .OrderBy(i => i.Label.StartsWith(prefix, StringComparison.Ordinal) ? 0 : i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(i => i.Priority)
            .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.Items.Clear();
        result.Items.AddRange(filtered);
    }

    /// <summary>Finds the start of the postfix expression ending right before <paramref name="end"/>.</summary>
    private static int FindExpressionStart(SemanticModel model, int end)
    {
        var tokens = model.Parse.Tokens.Where(t => t.Kind != TokenKind.Comment && t.End <= end).ToList();
        var i = tokens.Count - 1;
        if (i < 0) return -1;
        var start = -1;
        while (i >= 0)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Identifier)
            {
                start = t.Start;
                i--;
                if (i >= 0 && (tokens[i].IsOp(".") || tokens[i].IsOp(":")))
                {
                    i--;
                    continue;
                }
                break;
            }
            if (t.IsOp(")") || t.IsOp("]"))
            {
                var open = t.Text == ")" ? "(" : "[";
                var depth = 0;
                while (i >= 0)
                {
                    if (tokens[i].IsOp(t.Text)) depth++;
                    else if (tokens[i].IsOp(open))
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                    i--;
                }
                if (i < 0) return -1;
                start = tokens[i].Start;
                i--;
                // (expr) at the very start, or a call / index on something before
                if (i >= 0 && (tokens[i].Kind == TokenKind.Identifier || tokens[i].IsOp(")") || tokens[i].IsOp("]") || tokens[i].Kind == TokenKind.String)) continue;
                break;
            }
            if (t.Kind == TokenKind.String)
            {
                start = t.Start;
                break;
            }
            break;
        }
        return start;
    }

    private static Func<NameExpr, Symbol?> ResolverAt(SemanticModel model, int offset) =>
        n => model.ScopeAt(offset).Lookup(n.Name, offset);

    private static void AddMemberCompletions(SemanticModel model, CompletionResult result, string exprText, char sep, int offset)
    {
        var api = model.Api;
        var expr = SemanticModel.ParseExpression(exprText);
        if (expr == null) return;
        var type = model.TypeOf(expr, offset);
        var core = model.Context.TargetCore;

        if (sep == ':')
        {
            IEnumerable<ApiFunction> methods;
            if (type == "string") methods = api.FindLibrary("string")?.Functions ?? new List<ApiFunction>();
            else if (api.IsKnownObjectType(type)) methods = api.GetMethodsForType(type);
            else if (type == null || type == "table")
            {
                // Unknown type: offer user-defined methods of the table (self) and all Eluna methods.
                var fields = TypeEvaluator.FieldsOf(model, expr, ResolverAt(model, offset), 0);
                if (fields != null)
                {
                    foreach (var f in fields.Values.Where(f => f.Function != null))
                    {
                        result.Items.Add(new CompletionItem { Label = f.Name, Kind = CompletionKind.Method, Detail = FunctionLabel(f.Name, f.Function!), Priority = 5 });
                    }
                }
                if (type == "table" && fields?.Count > 0) return;
                methods = api.Classes.Where(c => c.Source == ApiSource.Eluna).SelectMany(c => c.Methods)
                    .GroupBy(m => m.Name).Select(g => g.First());
                foreach (var m in methods)
                {
                    var owners = api.Classes.Where(c => c.Methods.Any(x => x.Name == m.Name)).Select(c => c.Name).ToList();
                    result.Items.Add(new CompletionItem
                    {
                        Label = m.Name,
                        Kind = CompletionKind.Method,
                        Detail = $"({string.Join(", ", owners.Take(3))}{(owners.Count > 3 ? ", …" : "")}) {ParamList(m)}",
                        Documentation = m.ToMarkdownLikeText() + "\n\nTyp des Objekts unbekannt – Methode aus allen Eluna-Klassen.",
                        Function = m,
                        Priority = 40,
                    });
                }
                return;
            }
            else return;

            foreach (var m in methods)
            {
                var unavailable = core != null && m.Source == ApiSource.Eluna && m.Cores.Count > 0 && !m.Cores.Contains(core);
                result.Items.Add(new CompletionItem
                {
                    Label = m.Name,
                    Kind = CompletionKind.Method,
                    Detail = $"{ParamList(m)}{(m.Returns.Count > 0 ? " → " + string.Join(", ", m.Returns.Select(r => r.Type)) : "")}" + (m.Owner != null && m.Owner != type ? $"  [{m.Owner}]" : ""),
                    Documentation = m.ToMarkdownLikeText() + (unavailable ? $"\n\n⚠ Nicht verfügbar für {core}." : ""),
                    Function = m,
                    Priority = unavailable ? 80 : (m.Owner == type ? 10 : 20),
                    Unavailable = unavailable,
                });
            }
            return;
        }

        // '.' completion
        if (type != null && type.StartsWith("lib:"))
        {
            var lib = api.FindLibrary(type[4..]);
            if (lib == null) return;
            foreach (var f in lib.Functions)
            {
                result.Items.Add(new CompletionItem
                {
                    Label = f.Name,
                    Kind = CompletionKind.Function,
                    Detail = ParamList(f) + (f.Returns.Count > 0 ? " → " + f.Returns[0].Type : ""),
                    Documentation = f.ToMarkdownLikeText(),
                    Function = f,
                    Priority = 10,
                });
            }
            foreach (var fl in lib.Fields)
            {
                result.Items.Add(new CompletionItem { Label = fl.Name, Kind = CompletionKind.Field, Detail = fl.Type, Documentation = fl.Description, Priority = 15 });
            }
        }
        var tableFields = TypeEvaluator.FieldsOf(model, expr, ResolverAt(model, offset), 0);
        if (tableFields != null)
        {
            foreach (var f in tableFields.Values)
            {
                result.Items.Add(new CompletionItem
                {
                    Label = f.Name,
                    Kind = f.Function != null ? CompletionKind.Function : CompletionKind.Field,
                    Detail = f.Function != null ? FunctionLabel(f.Name, f.Function) : f.Type ?? "",
                    Priority = 5,
                });
            }
        }
        if (api.IsKnownObjectType(type))
        {
            // Eluna objects have no fields – suggest methods but insert them with ':' via the fix later.
            foreach (var m in api.GetMethodsForType(type))
            {
                result.Items.Add(new CompletionItem
                {
                    Label = m.Name,
                    Kind = CompletionKind.Method,
                    Detail = ParamList(m) + "  (Aufruf mit ':')",
                    Documentation = m.ToMarkdownLikeText(),
                    Function = m,
                    Priority = 60,
                });
            }
        }
    }

    private static string ParamList(ApiFunction f) => "(" + string.Join(", ", f.Params.Select(p => p.IsVarArgs ? "..." : p.Default != null ? p.Name + "?" : p.Name)) + ")";

    private static string FunctionLabel(string name, FunctionExpr f)
    {
        var ps = f.Params.Select(p => p.Name).ToList();
        if (f.IsVarArg) ps.Add("...");
        return $"function {name}({string.Join(", ", ps)})";
    }

    private static readonly string[] KeywordList =
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
        "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    };

    private static void AddGeneralCompletions(SemanticModel model, CompletionResult result, int offset, string? prevWord)
    {
        var api = model.Api;
        foreach (var kw in KeywordList)
        {
            result.Items.Add(new CompletionItem { Label = kw, Kind = CompletionKind.Keyword, Priority = 30 });
        }

        foreach (var sym in model.ScopeAt(offset).VisibleSymbols(offset))
        {
            if (sym.Name.Length == 0) continue;
            var kind = sym.Kind switch
            {
                SymbolKind.Parameter => CompletionKind.Parameter,
                SymbolKind.LocalFunction => CompletionKind.Function,
                _ => sym.Function != null ? CompletionKind.Function : CompletionKind.Local,
            };
            result.Items.Add(new CompletionItem
            {
                Label = sym.Name,
                Kind = kind,
                Detail = sym.Function != null ? FunctionLabel(sym.Name, sym.Function) : (sym.Type ?? "local"),
                Documentation = sym.Detail ?? "",
                Priority = 1,
            });
        }

        foreach (var g in model.GlobalDefinitions.Values.Concat(model.Context.AllProjectGlobals()))
        {
            result.Items.Add(new CompletionItem
            {
                Label = g.Name,
                Kind = g.IsFunction ? CompletionKind.Function : CompletionKind.Global,
                Detail = g.Function != null ? FunctionLabel(g.Name, g.Function) : (g.Type ?? "global"),
                Documentation = $"Global definiert in {Path.GetFileName(g.FilePath)}",
                Priority = 8,
            });
        }

        var core = model.Context.TargetCore;
        foreach (var f in api.Globals)
        {
            if (f.Source == ApiSource.WowClient && !model.IsClientFile) continue;
            var unavailable = core != null && f.Source == ApiSource.Eluna && f.Cores.Count > 0 && !f.Cores.Contains(core);
            result.Items.Add(new CompletionItem
            {
                Label = f.Name,
                Kind = CompletionKind.Function,
                Detail = ParamList(f) + (f.Returns.Count > 0 ? " → " + f.Returns[0].Type : "") + SourceTag(f.Source),
                Documentation = f.ToMarkdownLikeText(),
                Function = f,
                Priority = unavailable ? 85 : f.Source == ApiSource.Eluna ? 20 : 25,
                Unavailable = unavailable,
            });
        }
        foreach (var lib in api.Libraries)
        {
            result.Items.Add(new CompletionItem { Label = lib.Name, Kind = CompletionKind.Library, Detail = lib.Source == ApiSource.Aio ? "AIO" : "Bibliothek", Documentation = lib.Description, Priority = 22 });
        }
        foreach (var fl in api.GlobalFields)
        {
            if (fl.Source == ApiSource.WowClient && !model.IsClientFile) continue;
            result.Items.Add(new CompletionItem { Label = fl.Name, Kind = CompletionKind.Field, Detail = fl.Type, Documentation = fl.Description, Priority = 26 });
        }

        if (prevWord is not ("local" or "function" or "." or ":"))
        {
            foreach (var s in Snippets.All(model))
            {
                result.Items.Add(s);
            }
        }
    }

    // =========================================================================================
    // Call context (signature help, event id completion)
    // =========================================================================================

    public readonly record struct CallContext(int OpenParen, int ArgumentIndex, string CalleeText, bool IsMethod);

    /// <summary>Finds the call whose argument list contains the offset.</summary>
    public static CallContext? FindEnclosingCall(SemanticModel model, int offset)
    {
        var tokens = model.Parse.Tokens;
        var idx = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Start >= offset) break;
            idx = i;
        }
        var depth = 0;
        var arg = 0;
        for (var i = idx; i >= 0; i--)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Comment) continue;
            if (t.Kind == TokenKind.Keyword && t.Text is "function" or "end" or "do" or "then" or "local" or "return" && depth == 0) return null;
            if (t.Kind != TokenKind.Operator) continue;
            switch (t.Text)
            {
                case ")":
                case "]":
                case "}":
                    depth++;
                    break;
                case "[":
                case "{":
                    if (depth == 0) return null;
                    depth--;
                    break;
                case "(":
                    if (depth == 0)
                    {
                        if (i == 0 || tokens[i - 1].Kind != TokenKind.Identifier) return null;
                        var start = FindExpressionStart(model, tokens[i - 1].End);
                        if (start < 0) return null;
                        var calleeText = model.Text.Substring(start, tokens[i - 1].End - start);
                        var isMethod = i >= 2 && tokens[i - 2].IsOp(":");
                        return new CallContext(t.Start, arg, calleeText, isMethod);
                    }
                    depth--;
                    break;
                case ",":
                    if (depth == 0) arg++;
                    break;
            }
        }
        return null;
    }

    /// <summary>Resolves the API function of a call context (Eluna, AIO, stdlib or user function).</summary>
    public static ApiFunction? ResolveCall(SemanticModel model, CallContext call, int offset)
    {
        var expr = SemanticModel.ParseExpression(call.CalleeText + "()");
        var resolver = ResolverAt(model, offset);
        switch (expr)
        {
            case MethodCallExpr mc:
            {
                var type = model.TypeOf(mc.Object, offset, resolver);
                var fn = TypeEvaluator.FindMethod(model.Api, type, mc.Method.Name);
                if (fn != null) return fn;
                var fields = TypeEvaluator.FieldsOf(model, mc.Object, resolver, 0);
                if (fields != null && fields.TryGetValue(mc.Method.Name, out var fi) && fi.Function != null)
                {
                    return FromUserFunction(mc.Method.Name, fi.Function, skipSelf: !fi.Function.HasImplicitSelf);
                }
                return null;
            }
            case CallExpr c:
            {
                var fn = TypeEvaluator.ResolveCallee(model, c.Callee, resolver, 0);
                if (fn != null) return fn;
                if (c.Callee is NameExpr n)
                {
                    var sym = resolver(n);
                    if (sym?.Function != null) return FromUserFunction(n.Name, sym.Function, false);
                    var g = model.FindGlobal(n.Name);
                    if (g?.Function != null) return FromUserFunction(n.Name, g.Function, false);
                }
                if (c.Callee is MemberExpr m)
                {
                    var fields = TypeEvaluator.FieldsOf(model, m.Object, resolver, 0);
                    if (fields != null && fields.TryGetValue(m.Member.Name, out var fi) && fi.Function != null)
                    {
                        return FromUserFunction(call.CalleeText, fi.Function, false);
                    }
                }
                return null;
            }
        }
        return null;
    }

    private static ApiFunction FromUserFunction(string name, FunctionExpr f, bool skipSelf)
    {
        var fn = new ApiFunction { Name = name, Description = "Benutzerdefinierte Funktion", Source = ApiSource.LuaStdLib };
        foreach (var p in f.Params.Skip(skipSelf ? 1 : 0)) fn.Params.Add(new ApiParameter { Name = p.Name, Type = "any" });
        if (f.IsVarArg) fn.Params.Add(new ApiParameter { Name = "...", Type = "..." });
        return fn;
    }

    public static SignatureHelp? GetSignatureHelp(SemanticModel model, int offset)
    {
        if (IsInCommentOrString(model, offset) && FindEnclosingCall(model, offset) == null) return null;
        var call = FindEnclosingCall(model, offset);
        if (call == null) return null;
        var fn = ResolveCall(model, call.Value, offset);
        if (fn == null) return null;
        string? extra = null;
        if (fn.Owner == null && model.Api.FindHook(fn.Name) is { } hook && call.Value.ArgumentIndex == hook.EventArgIndex)
        {
            extra = string.Join("\n", hook.Events.Select(e => $"{e.Id,3} = {e.Name}({e.ParameterList})"));
        }
        return new SignatureHelp { Function = fn, ActiveParameter = call.Value.ArgumentIndex, Extra = extra };
    }

    private static void AddArgumentCompletions(SemanticModel model, CompletionResult result, CallContext call, int offset)
    {
        var fn = ResolveCall(model, call, offset);
        if (fn == null) return;
        if (fn.Owner == null && model.Api.FindHook(fn.Name) is { } hook)
        {
            if (call.ArgumentIndex == hook.EventArgIndex)
            {
                foreach (var e in hook.Events)
                {
                    result.Items.Add(new CompletionItem
                    {
                        Label = $"{e.Id} {e.Name}",
                        Kind = CompletionKind.Event,
                        InsertText = e.Id.ToString(),
                        Detail = $"function({e.ParameterList})",
                        Documentation = $"{e.Name} = {e.Id}\nState: {e.State}\nHandler: function({string.Join(", ", e.Params.Select(p => $"{p.Name}: {p.Type}"))})" +
                                        (string.IsNullOrWhiteSpace(e.Comment) ? "" : "\n" + e.Comment),
                        Priority = 0,
                    });
                }
            }
            else if (call.ArgumentIndex == hook.HandlerArgIndex)
            {
                var id = TryGetEventArgument(model, call, hook);
                var ev = id != null ? hook.FindEvent(id.Value) : null;
                var ps = ev != null ? ev.ParameterList : "event, ...";
                result.Items.Add(new CompletionItem
                {
                    Label = "function(" + ps + ")",
                    Kind = CompletionKind.Snippet,
                    InsertText = $"function({ps})\n\t$0\nend",
                    Detail = ev != null ? ev.Name : "Handler",
                    Documentation = "Fügt eine anonyme Handler-Funktion mit den passenden Parametern ein.",
                    Priority = 0,
                });
            }
        }
        // AIO.Handle(player, "Name", "Handler") – handler names are strings, handled in the editor.
    }

    private static int? TryGetEventArgument(SemanticModel model, CallContext call, ApiHook hook)
    {
        // Reparse the argument text between '(' and the caret.
        var argsText = model.Text.Substring(call.OpenParen + 1);
        var parts = SplitTopLevelArgs(argsText);
        if (parts.Count > hook.EventArgIndex && int.TryParse(parts[hook.EventArgIndex].Trim(), out var id)) return id;
        if (parts.Count > hook.EventArgIndex)
        {
            var name = parts[hook.EventArgIndex].Trim();
            var sym = model.ScopeAt(call.OpenParen).Lookup(name, call.OpenParen);
            if (sym != null)
            {
                var decl = model.Text.Substring(sym.DeclarationStart);
                var m = System.Text.RegularExpressions.Regex.Match(decl, @"^\w+\s*=\s*(\d+)");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            var ev = hook.Events.FirstOrDefault(e => e.Name == name);
            if (ev != null) return ev.Id;
        }
        return null;
    }

    private static List<string> SplitTopLevelArgs(string text)
    {
        var result = new List<string>();
        var depth = 0;
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']')
            {
                if (depth == 0) break;
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            else if (c == '\n' && depth == 0 && sb.ToString().Trim().Length == 0) { }
            sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    // =========================================================================================
    // Hover
    // =========================================================================================

    public static HoverInfo? GetHover(SemanticModel model, int offset)
    {
        var api = model.Api;
        var path = AstNodes.PathTo(model.Parse.Chunk, offset);
        if (path.Count == 0) return null;
        var node = path[^1];
        var parent = path.Count > 1 ? path[^2] : null;

        Func<NameExpr, Symbol?> resolver = n => model.FindSymbolAt(n.Start) is { } s && s.Name == n.Name ? s : model.ScopeAt(n.Start).Lookup(n.Name, n.Start);

        switch (node)
        {
            case Identifier id when parent is MethodCallExpr mc && mc.Method == id:
            {
                var type = TypeEvaluator.Evaluate(model, mc.Object, resolver);
                var fn = TypeEvaluator.FindMethod(api, type, id.Name);
                if (fn != null) return new HoverInfo { Title = fn.Signature, Body = BodyOf(fn), Start = id.Start, Length = id.Length };
                return new HoverInfo { Title = $"{type ?? "?"}:{id.Name}", Body = type == null ? "Typ des Objekts unbekannt." : "Unbekannte Methode.", Start = id.Start, Length = id.Length };
            }
            case Identifier id when parent is MemberExpr me && me.Member == id:
            {
                var type = TypeEvaluator.Evaluate(model, me.Object, resolver);
                if (type != null && type.StartsWith("lib:"))
                {
                    var lib = api.FindLibrary(type[4..]);
                    var fn = lib?.Functions.FirstOrDefault(f => f.Name == id.Name);
                    if (fn != null) return new HoverInfo { Title = fn.Signature, Body = BodyOf(fn), Start = id.Start, Length = id.Length };
                    var field = lib?.Fields.FirstOrDefault(f => f.Name == id.Name);
                    if (field != null) return new HoverInfo { Title = $"{lib!.Name}.{field.Name}: {field.Type}", Body = field.Description, Start = id.Start, Length = id.Length };
                }
                var fields = TypeEvaluator.FieldsOf(model, me.Object, resolver, 0);
                if (fields != null && fields.TryGetValue(id.Name, out var fi))
                {
                    var title = fi.Function != null ? FunctionLabel(id.Name, fi.Function) : $"Feld {id.Name}: {fi.Type ?? "?"}";
                    var handler = fi.Function != null && model.Handlers.TryGetValue(fi.Function, out var h) ? h.Description : "";
                    return new HoverInfo { Title = title, Body = handler, Start = id.Start, Length = id.Length };
                }
                if (api.FindMethod(type, id.Name) is { } m)
                {
                    return new HoverInfo { Title = m.Signature, Body = BodyOf(m) + "\n\nHinweis: Methoden werden mit ':' aufgerufen.", Start = id.Start, Length = id.Length };
                }
                return null;
            }
            case NameExpr ne:
                return HoverForName(model, ne.Name, ne.Start, ne.Length, resolver(ne));
            case Identifier id:
            {
                var sym = model.FindSymbolAt(id.Start);
                if (sym != null) return HoverForName(model, id.Name, id.Start, id.Length, sym);
                return null;
            }
            case LiteralExpr lit when lit.Kind == LiteralKind.Number:
            {
                // Event id of a Register*Event call
                foreach (var call in path.OfType<CallExpr>().Reverse())
                {
                    if (call.Callee is NameExpr cn && api.FindHook(cn.Name) is { } hook && call.Args.Count > hook.EventArgIndex && call.Args[hook.EventArgIndex] == lit)
                    {
                        var id = (int)(lit.NumberValue ?? -1);
                        var ev = hook.FindEvent(id);
                        if (ev != null) return EventHover(ev, lit.Start, lit.Length);
                        return new HoverInfo { Title = $"Ungültige Event-ID {id}", Body = $"Für {cn.Name} gibt es kein Event mit dieser ID.", Start = lit.Start, Length = lit.Length };
                    }
                }
                return null;
            }
        }
        return null;
    }

    private static HoverInfo EventHover(ApiEvent ev, int start, int length) => new()
    {
        Title = $"{ev.Name} = {ev.Id}",
        Body = $"Handler: function({string.Join(", ", ev.Params.Select(p => $"{p.Name}: {p.Type}"))})\nState: {ev.State}" +
               (string.IsNullOrWhiteSpace(ev.Comment) ? "" : "\n" + ev.Comment),
        Start = start,
        Length = length,
    };

    private static string BodyOf(ApiFunction fn)
    {
        var text = fn.ToMarkdownLikeText();
        var nl = text.IndexOf('\n');
        return nl >= 0 ? text[(nl + 1)..].Trim() : "";
    }

    private static HoverInfo? HoverForName(SemanticModel model, string name, int start, int length, Symbol? sym)
    {
        var api = model.Api;
        if (sym != null)
        {
            var kind = sym.Kind switch
            {
                SymbolKind.Parameter => "Parameter",
                SymbolKind.LocalFunction => "lokale Funktion",
                SymbolKind.LoopVariable => "Schleifenvariable",
                _ => "lokale Variable",
            };
            var title = sym.Function != null ? $"({kind}) {FunctionLabel(name, sym.Function)}" : $"({kind}) {name}: {sym.Type ?? "?"}";
            var body = new StringBuilder();
            if (sym.TypeFromName) body.AppendLine($"Typ aus dem Namen '{name}' abgeleitet.");
            if (sym.Detail != null) body.AppendLine(sym.Detail);
            if (sym.Function != null && model.Handlers.TryGetValue(sym.Function, out var h)) body.AppendLine(h.Description);
            if (api.IsClass(sym.Type) && api.FindClass(sym.Type) is { } cls && !string.IsNullOrWhiteSpace(cls.Description)) body.AppendLine(ApiText.StripLinks(cls.Description));
            return new HoverInfo { Title = title, Body = body.ToString().Trim(), Start = start, Length = length };
        }
        var g = model.FindGlobal(name);
        if (g != null && api.FindGlobal(name) == null)
        {
            var title = g.Function != null ? $"(global) {FunctionLabel(name, g.Function)}" : $"(global) {name}: {g.Type ?? "?"}";
            var body = $"Definiert in {Path.GetFileName(g.FilePath)}";
            if (g.Function != null && model.Handlers.TryGetValue(g.Function, out var h)) body += "\n" + h.Description;
            return new HoverInfo { Title = title, Body = body, Start = start, Length = length };
        }
        var fn = api.FindGlobal(name);
        if (fn != null) return new HoverInfo { Title = fn.Signature, Body = BodyOf(fn), Start = start, Length = length };
        var lib = api.FindLibrary(name);
        if (lib != null)
        {
            return new HoverInfo { Title = $"Bibliothek {name}", Body = lib.Description + $"\n{lib.Functions.Count} Funktionen", Start = start, Length = length };
        }
        var field = api.FindGlobalField(name);
        if (field != null) return new HoverInfo { Title = $"{name}: {field.Type}", Body = field.Description, Start = start, Length = length };
        return null;
    }

    // =========================================================================================
    // Go to definition / references
    // =========================================================================================

    public static DefinitionLocation? GetDefinition(SemanticModel model, int offset)
    {
        var path = AstNodes.PathTo(model.Parse.Chunk, offset);
        var node = path.LastOrDefault();
        var parent = path.Count > 1 ? path[^2] : null;
        var sym = model.FindSymbolAt(offset);
        if (sym != null && sym.Kind != SymbolKind.Global)
        {
            return new DefinitionLocation(model.FilePath, sym.DeclarationStart, sym.Name.Length);
        }
        if (node is NameExpr ne)
        {
            var g = model.FindGlobal(ne.Name);
            if (g != null) return new DefinitionLocation(g.FilePath, g.Offset, ne.Name.Length);
        }
        if (node is Identifier id && parent is MemberExpr me && me.Member == id)
        {
            Func<NameExpr, Symbol?> resolver = n => model.FindSymbolAt(n.Start) ?? model.ScopeAt(n.Start).Lookup(n.Name, n.Start);
            var fields = TypeEvaluator.FieldsOf(model, me.Object, resolver, 0);
            if (fields != null && fields.TryGetValue(id.Name, out var fi))
            {
                return new DefinitionLocation(fi.FilePath ?? model.FilePath, fi.Offset, id.Name.Length);
            }
        }
        return null;
    }

    /// <summary>All occurrences of the local symbol at the offset (declaration + references).</summary>
    public static List<(int Start, int Length)> GetLocalReferences(SemanticModel model, int offset)
    {
        var sym = model.FindSymbolAt(offset);
        if (sym == null) return new();
        var list = new List<(int, int)> { (sym.DeclarationStart, sym.Name.Length) };
        list.AddRange(sym.References.Where(r => r != sym.DeclarationStart).Select(r => (r, sym.Name.Length)));
        return list.Distinct().OrderBy(r => r.Item1).ToList();
    }

    private static string SourceTag(ApiSource s) => s switch
    {
        ApiSource.Aio => "  [AIO]",
        ApiSource.LuaStdLib => "  [Lua]",
        ApiSource.WowClient => "  [WoW]",
        _ => "",
    };
}
