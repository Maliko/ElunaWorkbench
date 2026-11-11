using ElunaWorkbench.Core.Api;
using ElunaWorkbench.Core.Lua;

namespace ElunaWorkbench.Core.Analysis;

/// <summary>
/// Semantic analysis of one Lua file: scopes, symbol resolution, type inference for Eluna
/// objects (including event handler parameters), and Eluna/AIO specific diagnostics.
/// </summary>
public sealed class Analyzer
{
    private static readonly Dictionary<string, string> ParameterNameTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["player"] = "Player", ["plr"] = "Player", ["killer"] = "Unit", ["victim"] = "Unit",
        ["creature"] = "Creature", ["npc"] = "Creature", ["boss"] = "Creature",
        ["unit"] = "Unit", ["target"] = "Unit", ["caster"] = "Unit",
        ["go"] = "GameObject", ["gameobject"] = "GameObject", ["gob"] = "GameObject",
        ["item"] = "Item", ["quest"] = "Quest", ["spell"] = "Spell", ["aura"] = "Aura",
        ["group"] = "Group", ["guild"] = "Guild", ["map"] = "Map", ["instance"] = "Map",
        ["packet"] = "WorldPacket", ["query"] = "ElunaQuery", ["corpse"] = "Corpse",
        ["vehicle"] = "Vehicle", ["bg"] = "BattleGround", ["battleground"] = "BattleGround",
        ["worldobject"] = "WorldObject", ["obj"] = "WorldObject", ["object"] = "WorldObject",
        ["msg"] = BuiltinApi.AioMessageClass,
    };

    private static readonly Dictionary<string, (string Replacement, string Message)> ForeignNames = new(StringComparer.Ordinal)
    {
        ["null"] = ("nil", "Lua verwendet 'nil' statt 'null'."),
        ["NULL"] = ("nil", "Lua verwendet 'nil' statt 'NULL'."),
        ["undefined"] = ("nil", "Lua verwendet 'nil' statt 'undefined'."),
        ["None"] = ("nil", "Lua verwendet 'nil' statt 'None'."),
        ["this"] = ("self", "Lua verwendet 'self' statt 'this'."),
        ["True"] = ("true", "Boolesche Werte werden in Lua klein geschrieben."),
        ["False"] = ("false", "Boolesche Werte werden in Lua klein geschrieben."),
        ["TRUE"] = ("true", "Boolesche Werte werden in Lua klein geschrieben."),
        ["FALSE"] = ("false", "Boolesche Werte werden in Lua klein geschrieben."),
    };

    private readonly AnalysisContext _ctx;
    private readonly ApiDatabase _api;
    private readonly ParseResult _parse;
    private readonly SemanticModel _model;
    private readonly Dictionary<Identifier, Symbol> _declarations = new();
    private readonly Dictionary<Symbol, Expr> _initValues = new();
    private readonly List<CallExpr> _calls = new();
    private readonly List<MethodCallExpr> _methodCalls = new();
    private readonly List<(NameExpr Name, bool InFunction)> _unresolvedReads = new();
    private readonly List<(NameExpr Name, AssignStmt Stmt, bool InFunction)> _globalWrites = new();
    private readonly Dictionary<FunctionExpr, Expr> _methodTargets = new();
    private readonly HashSet<FunctionExpr> _typedFunctions = new();

    private Analyzer(string text, AnalysisContext ctx)
    {
        _ctx = ctx;
        _api = ctx.Api;
        _parse = Parser.Parse(text);
        var root = new Scope(null, 0, text.Length);
        _model = new SemanticModel(_parse, ctx, root);
    }

    public static SemanticModel Analyze(string text, AnalysisContext? context = null)
    {
        var a = new Analyzer(text ?? string.Empty, context ?? new AnalysisContext());
        return a.Run();
    }

    private SemanticModel Run()
    {
        _model.IsClientFile = _ctx.IsClientFile ?? DetectClientFile();
        _model.Diagnostics.AddRange(_parse.Diagnostics);

        BindBlock(_parse.Chunk.Body, _model.RootScope);
        CollectOutline();
        ProcessRegistrations();
        CheckBlock(_parse.Chunk.Body);
        ReportUnresolvedGlobals();
        ReportUnusedLocals();
        ReportLuaVersionFeatures();

        foreach (var d in _model.Diagnostics)
        {
            if (d.Line == 0)
            {
                var (l, c) = _parse.Lines.GetPosition(d.Start);
                d.Line = l;
                d.Column = c;
            }
            d.FilePath = _ctx.FilePath;
        }
        _model.Diagnostics.Sort((a, b) => a.Start.CompareTo(b.Start));
        return _model;
    }

    private bool DetectClientFile()
    {
        var path = _ctx.FilePath.Replace('\\', '/').ToLowerInvariant();
        if (path.Contains("/client/") || path.Contains("/addons/") || path.Contains("_client")) return true;
        // AIO pattern: "if AIO.AddAddon() then return end" – the rest of the file is client code.
        foreach (var stmt in _parse.Chunk.Body.Statements)
        {
            if (stmt is IfStmt ifs && ifs.Clauses.Count > 0 && ContainsAddAddonCall(ifs.Clauses[0].Condition)
                && ifs.Clauses[0].Body.Statements.Any(s => s is ReturnStmt))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsAddAddonCall(Expr e) =>
        AstNodes.Descendants(e).OfType<CallExpr>().Any(c => c.Callee is MemberExpr { Member.Name: "AddAddon" });

    // =========================================================================================
    // Pass 1: binding
    // =========================================================================================

    private Symbol Declare(Scope scope, Identifier id, SymbolKind kind, int visibleFrom)
    {
        var sym = new Symbol { Name = id.Name, Kind = kind, DeclarationStart = id.Start, VisibleFrom = visibleFrom };
        scope.Declare(sym);
        _declarations[id] = sym;
        _model.AllSymbols.Add(sym);
        if (id.Name.Length > 0) _model.SymbolAt[id.Start] = sym;
        return sym;
    }

    private void BindBlock(Block block, Scope scope)
    {
        foreach (var stmt in block.Statements) BindStmt(stmt, scope);
    }

    private Scope NewScope(Scope parent, Node node) => new(parent, node.Start, node.End);

    private void BindStmt(Stmt stmt, Scope scope)
    {
        switch (stmt)
        {
            case LocalStmt l:
            {
                foreach (var v in l.Values) BindExpr(v, scope);
                for (var i = 0; i < l.Names.Count; i++)
                {
                    var sym = Declare(scope, l.Names[i], SymbolKind.Local, l.End);
                    if (i < l.Values.Count)
                    {
                        _initValues[sym] = l.Values[i];
                        sym.WriteCount++;
                        if (l.Values[i] is FunctionExpr fe)
                        {
                            sym.Function = fe;
                            fe.DisplayName ??= sym.Name;
                        }
                        if (l.Values[i] is TableExpr te) CollectTableFields(te, sym.Fields);
                    }
                }
                break;
            }
            case LocalFunctionStmt lf:
            {
                var sym = Declare(scope, lf.Name, SymbolKind.LocalFunction, lf.Name.Start);
                sym.Function = lf.Function;
                sym.Type = "function";
                sym.WriteCount++;
                BindFunction(lf.Function, scope);
                break;
            }
            case FunctionStmt fs:
            {
                BindFunctionTarget(fs, scope);
                BindFunction(fs.Function, scope);
                break;
            }
            case AssignStmt a:
            {
                foreach (var v in a.Values) BindExpr(v, scope);
                for (var i = 0; i < a.Targets.Count; i++)
                {
                    var target = a.Targets[i];
                    var value = i < a.Values.Count ? a.Values[i] : null;
                    BindAssignTarget(target, value, a, scope);
                }
                break;
            }
            case CallStmt c:
                BindExpr(c.Call, scope);
                break;
            case DoStmt d:
                BindBlock(d.Body, NewScope(scope, d.Body));
                break;
            case WhileStmt w:
                BindExpr(w.Condition, scope);
                BindBlock(w.Body, NewScope(scope, w.Body));
                break;
            case RepeatStmt r:
            {
                var s = new Scope(scope, r.Body.Start, r.End);
                BindBlock(r.Body, s);
                BindExpr(r.Condition, s);
                break;
            }
            case IfStmt i:
                foreach (var cl in i.Clauses)
                {
                    BindExpr(cl.Condition, scope);
                    BindBlock(cl.Body, NewScope(scope, cl.Body));
                }
                if (i.Else != null) BindBlock(i.Else, NewScope(scope, i.Else));
                break;
            case NumericForStmt nf:
            {
                BindExpr(nf.From, scope);
                BindExpr(nf.To, scope);
                if (nf.Step != null) BindExpr(nf.Step, scope);
                var s = new Scope(scope, nf.Variable.Start, nf.End);
                var sym = Declare(s, nf.Variable, SymbolKind.LoopVariable, nf.Variable.Start);
                sym.Type = "number";
                BindBlock(nf.Body, s);
                break;
            }
            case GenericForStmt gf:
            {
                foreach (var it in gf.Iterators) BindExpr(it, scope);
                var s = new Scope(scope, gf.Names.Count > 0 ? gf.Names[0].Start : gf.Start, gf.End);
                foreach (var n in gf.Names) Declare(s, n, SymbolKind.LoopVariable, n.Start);
                BindBlock(gf.Body, s);
                break;
            }
            case ReturnStmt rs:
                foreach (var v in rs.Values) BindExpr(v, scope);
                break;
            case LabelStmt ls:
                Declare(scope, ls.Label, SymbolKind.Label, ls.Start);
                break;
        }
    }

    private void BindFunctionTarget(FunctionStmt fs, Scope scope)
    {
        if (fs.Target is NameExpr n && fs.MethodName == null)
        {
            var sym = scope.Lookup(n.Name, n.Start);
            if (sym != null)
            {
                Reference(sym, n, write: true);
                sym.Function = fs.Function;
                sym.Type ??= "function";
            }
            else
            {
                var g = DefineGlobal(n.Name, n.Start);
                g.Function ??= fs.Function;
                g.Type ??= "function";
            }
            return;
        }
        // a.b.c or a.b:c
        Dictionary<string, FieldInfo>? fields;
        Identifier? fieldId;
        if (fs.MethodName != null)
        {
            fields = GetFieldsOf(fs.Target, scope);
            fieldId = fs.MethodName;
            _methodTargets[fs.Function] = fs.Target;
        }
        else if (fs.Target is MemberExpr tm)
        {
            fields = GetFieldsOf(tm.Object, scope);
            fieldId = tm.Member;
        }
        else
        {
            return;
        }
        if (fields != null && fieldId.Name.Length > 0)
        {
            if (!fields.TryGetValue(fieldId.Name, out var fi))
            {
                fi = new FieldInfo { Name = fieldId.Name, Offset = fieldId.Start, FilePath = _ctx.FilePath };
                fields[fieldId.Name] = fi;
            }
            fi.Function = fs.Function;
            fi.Type = "function";
        }
    }

    /// <summary>Returns the (mutable) field table that represents the value of the expression.</summary>
    private Dictionary<string, FieldInfo>? GetFieldsOf(Expr e, Scope scope)
    {
        switch (e)
        {
            case NameExpr n:
                return FieldsForName(n, scope);
            case MemberExpr m:
            {
                var parent = GetFieldsOf(m.Object, scope);
                return parent == null || m.Member.Name.Length == 0 ? null : GetOrAddField(parent, m.Member);
            }
            default:
                BindExpr(e, scope);
                return null;
        }
    }

    private Dictionary<string, FieldInfo>? FieldsForName(NameExpr n, Scope scope)
    {
        var sym = scope.Lookup(n.Name, n.Start);
        if (sym != null)
        {
            Reference(sym, n, write: false);
            return sym.Fields;
        }
        _model.GlobalReads.Add(n.Name);
        if (_api.FindLibrary(n.Name) != null && !_model.GlobalDefinitions.ContainsKey(n.Name)) return null;
        var g = DefineGlobal(n.Name, n.Start, isWrite: false);
        return g.Fields;
    }

    private Dictionary<string, FieldInfo> GetOrAddField(Dictionary<string, FieldInfo> fields, Identifier member)
    {
        if (!fields.TryGetValue(member.Name, out var fi))
        {
            fi = new FieldInfo { Name = member.Name, Offset = member.Start, FilePath = _ctx.FilePath, Type = "table" };
            fields[member.Name] = fi;
        }
        return fi.Fields;
    }

    private GlobalDefinition DefineGlobal(string name, int offset, bool isWrite = true)
    {
        if (!_model.GlobalDefinitions.TryGetValue(name, out var g))
        {
            g = new GlobalDefinition { Name = name, FilePath = _ctx.FilePath, Offset = offset };
            if (isWrite || _ctx.ProjectGlobals(name) == null)
            {
                _model.GlobalDefinitions[name] = g;
            }
            else
            {
                // Field assignment to a global defined in another file: record fields on that definition copy.
                _model.GlobalDefinitions[name] = g;
            }
        }
        return g;
    }

    private void BindAssignTarget(Expr target, Expr? value, AssignStmt stmt, Scope scope)
    {
        switch (target)
        {
            case NameExpr n:
            {
                var sym = scope.Lookup(n.Name, n.Start);
                if (sym != null)
                {
                    Reference(sym, n, write: true);
                    if (value is FunctionExpr fe) { sym.Function ??= fe; fe.DisplayName ??= n.Name; }
                    if (value is TableExpr te) CollectTableFields(te, sym.Fields);
                    if (value != null && !_initValues.ContainsKey(sym)) _initValues[sym] = value;
                }
                else
                {
                    var existed = _model.GlobalDefinitions.ContainsKey(n.Name);
                    var g = DefineGlobal(n.Name, n.Start);
                    if (value is FunctionExpr fe) { g.Function ??= fe; fe.DisplayName ??= n.Name; }
                    if (value is TableExpr te) CollectTableFields(te, g.Fields);
                    if (value != null && g.Type == null) g.Type = QuickType(value);
                    if (!existed) _globalWrites.Add((n, stmt, scope.Function != null));
                }
                break;
            }
            case MemberExpr m:
            {
                var fields = GetFieldsOf(m.Object, scope);
                if (fields != null && m.Member.Name.Length > 0)
                {
                    if (!fields.TryGetValue(m.Member.Name, out var fi))
                    {
                        fi = new FieldInfo { Name = m.Member.Name, Offset = m.Member.Start, FilePath = _ctx.FilePath };
                        fields[m.Member.Name] = fi;
                    }
                    if (value is FunctionExpr fe) { fi.Function = fe; fi.Type = "function"; }
                    else if (value is TableExpr te) { fi.Type = "table"; CollectTableFields(te, fi.Fields); }
                    else if (value != null) fi.Type ??= QuickType(value);
                }
                break;
            }
            case IndexExpr ix:
                BindExpr(ix.Object, scope);
                BindExpr(ix.Key, scope);
                break;
            default:
                BindExpr(target, scope);
                break;
        }
    }

    private string? QuickType(Expr value) => value switch
    {
        LiteralExpr { Kind: LiteralKind.String } => "string",
        LiteralExpr { Kind: LiteralKind.Number } => "number",
        LiteralExpr { Kind: LiteralKind.True or LiteralKind.False } => "boolean",
        TableExpr => "table",
        FunctionExpr => "function",
        _ => null,
    };

    private void CollectTableFields(TableExpr table, Dictionary<string, FieldInfo> fields)
    {
        foreach (var f in table.Fields)
        {
            string? key = f.Name?.Name;
            if (key == null && f.Key is LiteralExpr { Kind: LiteralKind.String } s) key = s.StringValue;
            if (string.IsNullOrEmpty(key)) continue;
            var fi = new FieldInfo { Name = key, Offset = f.Start, FilePath = _ctx.FilePath, Type = QuickType(f.Value) };
            if (f.Value is FunctionExpr fe)
            {
                fi.Function = fe;
                fe.DisplayName ??= key;
            }
            if (f.Value is TableExpr te) CollectTableFields(te, fi.Fields);
            fields[key] = fi;
        }
    }

    private void Reference(Symbol sym, NameExpr n, bool write)
    {
        _model.SymbolAt[n.Start] = sym;
        sym.References.Add(n.Start);
        if (write) sym.WriteCount++;
        else sym.ReadCount++;
    }

    private void BindFunction(FunctionExpr f, Scope parent)
    {
        var scope = new Scope(parent, f.Start, f.End, f);
        if (f.HasImplicitSelf)
        {
            var self = new Symbol { Name = "self", Kind = SymbolKind.Parameter, DeclarationStart = f.Start, VisibleFrom = f.Start };
            scope.Declare(self);
            self.ReadCount = 1;
            _model.AllSymbols.Add(self);
        }
        foreach (var p in f.Params) Declare(scope, p, SymbolKind.Parameter, f.Start);
        BindBlock(f.Body, scope);
    }

    private void BindExpr(Expr expr, Scope scope)
    {
        switch (expr)
        {
            case NameExpr n:
            {
                var sym = scope.Lookup(n.Name, n.Start);
                if (sym != null) Reference(sym, n, write: false);
                else
                {
                    _model.GlobalReads.Add(n.Name);
                    _unresolvedReads.Add((n, scope.Function != null));
                }
                break;
            }
            case FunctionExpr f:
                BindFunction(f, scope);
                break;
            case CallExpr c:
                _calls.Add(c);
                BindExpr(c.Callee, scope);
                foreach (var a in c.Args) BindExpr(a, scope);
                break;
            case MethodCallExpr mc:
                _methodCalls.Add(mc);
                BindExpr(mc.Object, scope);
                foreach (var a in mc.Args) BindExpr(a, scope);
                break;
            default:
                foreach (var child in AstNodes.Children(expr))
                {
                    if (child is Expr e) BindExpr(e, scope);
                    else if (child is TableField tf)
                    {
                        if (tf.Key != null) BindExpr(tf.Key, scope);
                        BindExpr(tf.Value, scope);
                    }
                }
                break;
        }
    }

    private void CollectOutline()
    {
        foreach (var node in AstNodes.Descendants(_parse.Chunk))
        {
            if (node is FunctionExpr f && f.DisplayName != null)
            {
                _model.Outline.Add((f.DisplayName, f.Start));
            }
        }
        _model.Outline.Sort((a, b) => a.Offset.CompareTo(b.Offset));
    }

    // =========================================================================================
    // Pass 1.5: event registrations (gives handler parameters their types)
    // =========================================================================================

    private Symbol? Resolved(NameExpr n) => _model.SymbolAt.TryGetValue(n.Start, out var s) && s.Name == n.Name ? s : null;

    private FunctionExpr? ResolveFunction(Expr e)
    {
        switch (e)
        {
            case FunctionExpr f:
                return f;
            case NameExpr n:
            {
                var sym = Resolved(n);
                if (sym != null) return sym.Function ?? (_initValues.TryGetValue(sym, out var init) ? init as FunctionExpr : null);
                return _model.GlobalDefinitions.TryGetValue(n.Name, out var g) ? g.Function : null;
            }
            case MemberExpr m:
            {
                var fields = TypeEvaluator.FieldsOf(_model, m.Object, Resolved, 0);
                return fields != null && fields.TryGetValue(m.Member.Name, out var fi) ? fi.Function : null;
            }
        }
        return null;
    }

    /// <summary>Evaluates a constant integer argument (literal or local constant).</summary>
    private int? ConstantInt(Expr e, int depth = 0)
    {
        if (depth > 5) return null;
        switch (e)
        {
            case LiteralExpr { Kind: LiteralKind.Number } lit:
                var v = lit.NumberValue;
                return v.HasValue && Math.Abs(v.Value % 1) < double.Epsilon ? (int)v.Value : null;
            case NameExpr n:
            {
                var sym = Resolved(n);
                if (sym != null && sym.WriteCount <= 1 && _initValues.TryGetValue(sym, out var init)) return ConstantInt(init, depth + 1);
                return null;
            }
            case MemberExpr m:
            {
                // e.g. PLAYER_EVENTS.ON_LOGIN where PLAYER_EVENTS = { ON_LOGIN = 3 }
                var fields = TypeEvaluator.FieldsOf(_model, m.Object, Resolved, 0);
                if (fields == null || !fields.ContainsKey(m.Member.Name)) return null;
                var table = FindTableConstructor(m.Object);
                var field = table?.Fields.FirstOrDefault(f => f.Name?.Name == m.Member.Name);
                return field != null ? ConstantInt(field.Value, depth + 1) : null;
            }
            case ParenExpr p:
                return ConstantInt(p.Inner, depth + 1);
        }
        return null;
    }

    private TableExpr? FindTableConstructor(Expr e)
    {
        if (e is NameExpr n)
        {
            var sym = Resolved(n);
            if (sym != null && _initValues.TryGetValue(sym, out var init)) return init as TableExpr;
        }
        return null;
    }

    private void ProcessRegistrations()
    {
        foreach (var call in _calls)
        {
            if (call.Callee is NameExpr callee && Resolved(callee) == null)
            {
                var hook = _api.FindHook(callee.Name);
                if (hook != null)
                {
                    ProcessHook(call, callee.Name, hook);
                    continue;
                }
                if (callee.Name == "CreateLuaEvent" && call.Args.Count > 0 && ResolveFunction(call.Args[0]) is { } timed)
                {
                    SetHandler(timed, new HandlerInfo { Description = "Timer-Callback von CreateLuaEvent(eventId, delay, repeats)" }, "number", "number", "number");
                }
            }
            else if (call.Callee is MemberExpr { Member.Name: var member } me && IsAioRef(me.Object))
            {
                ProcessAio(call, member);
            }
        }
    }

    private bool IsAioRef(Expr e)
    {
        if (e is not NameExpr n || n.Name != "AIO") return false;
        var sym = Resolved(n);
        return sym == null || _initValues.TryGetValue(sym, out var init) && TypeEvaluator.Evaluate(_model, init, Resolved) == "lib:AIO";
    }

    private void ProcessHook(CallExpr call, string registerName, ApiHook hook)
    {
        if (call.Args.Count <= hook.HandlerArgIndex) return;
        var handler = ResolveFunction(call.Args[hook.HandlerArgIndex]);
        if (handler == null) return;
        ApiEvent? ev = null;
        if (call.Args.Count > hook.EventArgIndex)
        {
            var id = ConstantInt(call.Args[hook.EventArgIndex]);
            if (id != null) ev = hook.FindEvent(id.Value);
        }
        var info = new HandlerInfo
        {
            Description = ev != null
                ? $"Handler für {ev.Name} ({registerName}, Event {ev.Id}): function({ev.ParameterList})"
                : $"Handler für {registerName}",
            Event = ev,
        };
        if (ev != null)
        {
            foreach (var p in ev.Params) info.ParameterTypes.Add(p.Type == "number" ? "number" : _api.ToLuaType(p.Type) is var t && t != "any" ? t : null);
        }
        else
        {
            // Unknown event: use the parameter types common to all events of this hook.
            var max = hook.Events.Count == 0 ? 0 : hook.Events.Max(e => e.Params.Count);
            for (var i = 0; i < max; i++)
            {
                var types = hook.Events.Where(e => e.Params.Count > i).Select(e => e.Params[i].Type).Distinct().ToList();
                info.ParameterTypes.Add(types.Count == 1 ? _api.ToLuaType(types[0]) : null);
            }
        }
        SetHandler(handler, info);
    }

    private void ProcessAio(CallExpr call, string member)
    {
        switch (member)
        {
            case "AddHandlers":
            {
                if (call.Args.Count < 2) return;
                var name = (call.Args[0] as LiteralExpr)?.StringValue;
                var reg = name != null ? new AioRegistration { Name = name, FilePath = _ctx.FilePath, Offset = call.Start, IsClient = _model.IsClientFile } : null;
                Dictionary<string, FieldInfo>? fields = null;
                if (call.Args[1] is TableExpr te)
                {
                    fields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
                    CollectTableFields(te, fields);
                }
                else if (call.Args[1] is NameExpr tn)
                {
                    fields = Resolved(tn)?.Fields ?? (_model.GlobalDefinitions.TryGetValue(tn.Name, out var g) ? g.Fields : null);
                }
                // local Handlers = AIO.AddHandlers("Name", {}) – handlers are added to the local later.
                var owner = _initValues.FirstOrDefault(kv => kv.Value == call).Key;
                if (owner != null)
                {
                    fields ??= new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
                    foreach (var kv in owner.Fields) fields.TryAdd(kv.Key, kv.Value);
                    foreach (var kv in fields) owner.Fields.TryAdd(kv.Key, kv.Value);
                }
                if (fields != null)
                {
                    foreach (var f in fields.Values)
                    {
                        reg?.HandlerNames.Add(f.Name);
                        if (f.Function != null) SetAioHandler(f.Function, $"AIO-Handler '{name}.{f.Name}' – aufgerufen mit (player, ...)");
                    }
                }
                else if (reg != null)
                {
                    reg.HandlersKnown = false;
                }
                // Handlers added later in the file via "function Handlers.X(...)" are already in fields (binding pass).
                if (reg != null) _model.AioRegistrations.Add(reg);
                break;
            }
            case "RegisterEvent":
            {
                if (call.Args.Count < 2) return;
                var name = (call.Args[0] as LiteralExpr)?.StringValue;
                if (name != null) _model.AioRegistrations.Add(new AioRegistration { Name = name, FilePath = _ctx.FilePath, Offset = call.Start, HandlersKnown = false, IsClient = _model.IsClientFile });
                var fn = ResolveFunction(call.Args[1]);
                if (fn != null) SetAioHandler(fn, $"AIO-Event '{name}' – aufgerufen mit (player, ...)");
                break;
            }
            case "AddOnInit":
            {
                if (call.Args.Count < 1) return;
                var fn = ResolveFunction(call.Args[0]);
                if (fn != null) SetHandler(fn, new HandlerInfo { Description = "AIO OnInit-Callback: function(msg, player)" }, BuiltinApi.AioMessageClass, "Player");
                break;
            }
        }
    }

    private void SetAioHandler(FunctionExpr fn, string description)
    {
        if (_model.IsClientFile)
        {
            SetHandler(fn, new HandlerInfo { Description = description });
        }
        else
        {
            SetHandler(fn, new HandlerInfo { Description = description }, "Player");
        }
    }

    private void SetHandler(FunctionExpr fn, HandlerInfo info, params string?[] types)
    {
        if (_model.Handlers.ContainsKey(fn)) return;
        foreach (var t in types) info.ParameterTypes.Add(t);
        _model.Handlers[fn] = info;
    }

    // =========================================================================================
    // Pass 2: type inference + diagnostics
    // =========================================================================================

    private void AddDiagnostic(Diagnostic d) => _model.Diagnostics.Add(d);

    private Diagnostic Diag(DiagnosticSeverity sev, string code, string message, Node node) =>
        Diag(sev, code, message, node.Start, Math.Max(1, node.Length));

    private Diagnostic Diag(DiagnosticSeverity sev, string code, string message, int start, int length)
    {
        var d = new Diagnostic(sev, code, message, start, length);
        AddDiagnostic(d);
        return d;
    }

    private string? TypeOf(Expr e) => TypeEvaluator.Evaluate(_model, e, Resolved);

    private void CheckBlock(Block block)
    {
        foreach (var s in block.Statements) CheckStmt(s);
    }

    private void CheckStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case LocalStmt l:
                foreach (var v in l.Values) CheckExpr(v);
                AssignTypes(l.Names.Select(n => _declarations.TryGetValue(n, out var s) ? s : null).ToList(), l.Values);
                break;
            case LocalFunctionStmt lf:
                CheckFunction(lf.Function);
                break;
            case FunctionStmt fs:
                CheckExpr(fs.Target, isWriteTarget: true);
                CheckFunction(fs.Function);
                break;
            case AssignStmt a:
                foreach (var v in a.Values) CheckExpr(v);
                foreach (var t in a.Targets) CheckExpr(t, isWriteTarget: true);
                AssignTypes(a.Targets.Select(t => t is NameExpr n ? Resolved(n) : null).ToList(), a.Values, onlyIfUnknown: true);
                break;
            case CallStmt c:
                CheckExpr(c.Call);
                break;
            case DoStmt d:
                CheckBlock(d.Body);
                break;
            case WhileStmt w:
                CheckExpr(w.Condition);
                CheckBlock(w.Body);
                break;
            case RepeatStmt r:
                CheckBlock(r.Body);
                CheckExpr(r.Condition);
                break;
            case IfStmt i:
                foreach (var cl in i.Clauses)
                {
                    CheckExpr(cl.Condition);
                    CheckBlock(cl.Body);
                }
                if (i.Else != null) CheckBlock(i.Else);
                break;
            case NumericForStmt nf:
                CheckExpr(nf.From);
                CheckExpr(nf.To);
                if (nf.Step != null) CheckExpr(nf.Step);
                CheckBlock(nf.Body);
                break;
            case GenericForStmt gf:
                foreach (var it in gf.Iterators) CheckExpr(it);
                InferGenericForTypes(gf);
                CheckBlock(gf.Body);
                break;
            case ReturnStmt rs:
                foreach (var v in rs.Values) CheckExpr(v);
                break;
        }
    }

    private void InferGenericForTypes(GenericForStmt gf)
    {
        // for i, v in ipairs(...) → i: number
        if (gf.Iterators.Count == 1 && gf.Iterators[0] is CallExpr { Callee: NameExpr { Name: "ipairs" } } && gf.Names.Count > 0
            && _declarations.TryGetValue(gf.Names[0], out var idx))
        {
            idx.Type = "number";
        }
        // for _, player in ipairs(GetPlayersInWorld()) → player: Player
        if (gf.Iterators.Count == 1 && gf.Iterators[0] is CallExpr { Callee: NameExpr { Name: "ipairs" or "pairs" } } it && it.Args.Count == 1 && gf.Names.Count > 1
            && _declarations.TryGetValue(gf.Names[1], out var val))
        {
            var elementType = TableElementType(it.Args[0]);
            if (elementType != null) val.Type = elementType;
            else TryTypeFromName(val);
        }
        foreach (var n in gf.Names)
        {
            if (_declarations.TryGetValue(n, out var s) && s.Type == null && n.Name.Length > 1) TryTypeFromName(s);
        }
    }

    /// <summary>Element type for tables returned by Eluna functions such as GetPlayersInWorld().</summary>
    private string? TableElementType(Expr e)
    {
        ApiFunction? fn = e switch
        {
            CallExpr c => TypeEvaluator.ResolveCallee(_model, c.Callee, Resolved, 0),
            MethodCallExpr mc => TypeEvaluator.FindMethod(_api, TypeOf(mc.Object), mc.Method.Name),
            _ => null,
        };
        if (fn == null || fn.ReturnType != "table") return null;
        var text = (fn.Returns[0].Name + " " + fn.Returns[0].Description + " " + fn.Description).ToLowerInvariant();
        if (text.Contains("player")) return "Player";
        if (text.Contains("creature")) return "Creature";
        if (text.Contains("gameobject")) return "GameObject";
        if (text.Contains("unit")) return "Unit";
        if (text.Contains("item")) return "Item";
        if (text.Contains("worldobject")) return "WorldObject";
        return null;
    }

    private void TryTypeFromName(Symbol s)
    {
        if (!_ctx.Options.InferTypesFromParameterNames || s.Type != null) return;
        if (ParameterNameTypes.TryGetValue(s.Name, out var t) && _api.IsClass(t))
        {
            s.Type = t;
            s.TypeFromName = true;
        }
    }

    private void AssignTypes(List<Symbol?> targets, List<Expr> values, bool onlyIfUnknown = false)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            var sym = targets[i];
            if (sym == null) continue;
            if (onlyIfUnknown && sym.Type != null && !sym.TypeFromName) continue;
            string? t = null;
            if (i < values.Count) t = TypeOf(values[i]);
            else if (values.Count > 0 && i >= values.Count)
            {
                // local x, y, z = obj:GetLocation() – additional return values
                var last = values[^1];
                ApiFunction? fn = last switch
                {
                    CallExpr c => TypeEvaluator.ResolveCallee(_model, c.Callee, Resolved, 0),
                    MethodCallExpr mc => TypeEvaluator.FindMethod(_api, TypeOf(mc.Object), mc.Method.Name),
                    _ => null,
                };
                var ri = i - values.Count + 1;
                if (fn != null && ri < fn.Returns.Count)
                {
                    var lt = _api.ToLuaType(fn.Returns[ri].Type);
                    t = lt == "any" ? null : lt;
                }
            }
            if (t == "nil") t = null;
            if (t != null)
            {
                sym.Type = t;
                sym.TypeFromName = false;
            }
            else if (sym.Type == null && sym.Kind == SymbolKind.Local && i < values.Count && values[i] is not LiteralExpr)
            {
                TryTypeFromName(sym);
            }
        }
    }

    private void CheckFunction(FunctionExpr f)
    {
        _model.Handlers.TryGetValue(f, out var handler);
        var scope = _model.RootScope.FindScope(f.Body.Start >= f.Start ? f.Start + 1 : f.Start);
        // Find the scope that belongs exactly to this function
        var fnScope = FindFunctionScope(_model.RootScope, f) ?? scope;
        for (var i = 0; i < f.Params.Count; i++)
        {
            if (!_declarations.TryGetValue(f.Params[i], out var sym)) continue;
            if (handler != null && i < handler.ParameterTypes.Count && handler.ParameterTypes[i] != null)
            {
                sym.Type = handler.ParameterTypes[i];
                if (handler.Event != null && i < handler.Event.Params.Count)
                {
                    sym.Detail = $"{handler.Event.Params[i].Name}: {handler.Event.Params[i].Type} ({handler.Event.Name})";
                }
            }
            else
            {
                TryTypeFromName(sym);
            }
        }
        if (f.HasImplicitSelf && _methodTargets.TryGetValue(f, out var target))
        {
            var self = fnScope.Symbols.FirstOrDefault(s => s.Name == "self");
            if (self != null)
            {
                var t = TypeOf(target);
                self.Type = t;
                if (TypeEvaluator.FieldsOf(_model, target, Resolved, 0) is { } fields)
                {
                    foreach (var kv in fields) self.Fields[kv.Key] = kv.Value;
                }
            }
        }
        if (handler != null && handler.Event != null && f.Params.Count > handler.Event.Params.Count && !f.IsVarArg)
        {
            var extra = f.Params[handler.Event.Params.Count];
            Diag(DiagnosticSeverity.Info, "ELU020",
                $"{handler.Event.Name} übergibt nur {handler.Event.Params.Count} Parameter ({handler.Event.ParameterList}); '{extra.Name}' ist immer nil.", extra);
        }
        CheckBlock(f.Body);
    }

    private static Scope? FindFunctionScope(Scope scope, FunctionExpr f)
    {
        if (scope.Function == f && scope.Start == f.Start) return scope;
        foreach (var c in scope.Children)
        {
            if (f.Start >= c.Start && f.End <= c.End)
            {
                var r = FindFunctionScope(c, f);
                if (r != null) return r;
            }
        }
        return null;
    }

    private void CheckExpr(Expr expr, bool isWriteTarget = false)
    {
        switch (expr)
        {
            case NameExpr:
                break;
            case FunctionExpr f:
                CheckFunction(f);
                break;
            case CallExpr call:
                CheckExpr(call.Callee);
                CheckCall(call);
                foreach (var a in call.Args) CheckExpr(a);
                break;
            case MethodCallExpr mc:
                CheckExpr(mc.Object);
                PrepareMethodCallbacks(mc);
                foreach (var a in mc.Args) CheckExpr(a);
                CheckMethodCall(mc);
                break;
            case MemberExpr m:
                CheckExpr(m.Object);
                if (!isWriteTarget) CheckMemberAccess(m);
                break;
            default:
                foreach (var child in AstNodes.Children(expr))
                {
                    if (child is Expr e) CheckExpr(e);
                    else if (child is TableField tf)
                    {
                        if (tf.Key != null) CheckExpr(tf.Key);
                        CheckExpr(tf.Value);
                    }
                }
                break;
        }
    }

    /// <summary>obj:RegisterEvent(function(eventId, delay, repeats, obj) ... end, delay)</summary>
    private void PrepareMethodCallbacks(MethodCallExpr mc)
    {
        if (mc.Method.Name != "RegisterEvent" || mc.Args.Count == 0) return;
        var objType = TypeOf(mc.Object);
        if (!_api.IsKnownObjectType(objType)) return;
        var fn = ResolveFunction(mc.Args[0]);
        if (fn != null) SetHandler(fn, new HandlerInfo { Description = $"Timer-Callback von {objType}:RegisterEvent(eventId, delay, repeats, worldobject)" }, "number", "number", "number", objType);
    }

    private void CheckCall(CallExpr call)
    {
        // Eluna method called with '.' instead of ':'
        if (call.Callee is MemberExpr member)
        {
            var objType = TypeOf(member.Object);
            if (objType != null && !objType.StartsWith("lib:") && _api.IsKnownObjectType(objType))
            {
                var method = _api.FindMethod(objType, member.Member.Name);
                var selfPassed = call.Args.Count > 0 && SameExpression(call.Args[0], member.Object);
                if (method != null && !selfPassed)
                {
                    var dotOffset = FindSeparator(member.Object.End, member.Member.Start, '.');
                    var d = Diag(DiagnosticSeverity.Error, "ELU002",
                        $"'{member.Member.Name}' ist eine Methode von {objType} und muss mit ':' aufgerufen werden.", member.Member);
                    if (dotOffset >= 0) d.Fixes.Add(new CodeFix("'.' durch ':' ersetzen", new TextEdit(dotOffset, 1, ":")));
                    return;
                }
            }
        }

        var fn = TypeEvaluator.ResolveCallee(_model, call.Callee, Resolved, 0);
        if (fn == null) return;
        CheckArgumentCount(fn, call.Args.Count, call.Callee is MemberExpr me ? me.Member : call.Callee, call.Args);
        CheckCoreAvailability(fn, call.Callee is MemberExpr me2 ? me2.Member : call.Callee);

        if (call.Callee is NameExpr name)
        {
            var hook = _api.FindHook(name.Name);
            if (hook != null) CheckHookCall(call, name.Name, hook);
            CheckVersionSpecificGlobal(name);
        }
        else if (call.Callee is MemberExpr { Object: NameExpr lib } m)
        {
            if (lib.Name == "table" && m.Member.Name is "unpack" or "pack" && _ctx.LuaVersion.Is51Like() && Resolved(lib) == null)
            {
                var d = Diag(DiagnosticSeverity.Warning, "ELU030", $"table.{m.Member.Name} existiert in {_ctx.LuaVersion.DisplayName()} nicht.", m);
                if (m.Member.Name == "unpack") d.Fixes.Add(new CodeFix("Durch 'unpack' ersetzen", new TextEdit(m.Start, m.Length, "unpack")));
            }
            if (IsAioRef(lib)) CheckAioCall(call, m.Member.Name);
        }
    }

    private void CheckVersionSpecificGlobal(NameExpr name)
    {
        if (Resolved(name) != null || _model.FindGlobal(name.Name) != null) return;
        if (_ctx.LuaVersion.Is51Like()) return;
        switch (name.Name)
        {
            case "unpack":
            {
                var d = Diag(DiagnosticSeverity.Warning, "ELU030", $"'unpack' ist in {_ctx.LuaVersion.DisplayName()} nicht global verfügbar. Verwenden Sie 'table.unpack'.", name);
                d.Fixes.Add(new CodeFix("Durch 'table.unpack' ersetzen", new TextEdit(name.Start, name.Length, "table.unpack")));
                break;
            }
            case "loadstring":
            {
                var d = Diag(DiagnosticSeverity.Warning, "ELU030", $"'loadstring' existiert in {_ctx.LuaVersion.DisplayName()} nicht. Verwenden Sie 'load'.", name);
                d.Fixes.Add(new CodeFix("Durch 'load' ersetzen", new TextEdit(name.Start, name.Length, "load")));
                break;
            }
            case "setfenv":
            case "getfenv":
            case "module":
                Diag(DiagnosticSeverity.Warning, "ELU030", $"'{name.Name}' existiert nur in Lua 5.1/LuaJIT.", name);
                break;
        }
    }

    private static bool SameExpression(Expr a, Expr b) => a is NameExpr na && b is NameExpr nb && na.Name == nb.Name;

    private int FindSeparator(int from, int to, char sep)
    {
        var text = _parse.Text;
        for (var i = Math.Max(0, from); i < Math.Min(to, text.Length); i++)
        {
            if (text[i] == sep) return i;
        }
        return -1;
    }

    private void CheckArgumentCount(ApiFunction fn, int count, Node anchor, List<Expr> args)
    {
        if (!_ctx.Options.ReportArgumentCount) return;
        // A trailing call or vararg can expand to any number of values.
        var lastIsMulti = args.Count > 0 && args[^1] is CallExpr or MethodCallExpr or LiteralExpr { Kind: LiteralKind.VarArgs };
        if (count < fn.MinArgs && !lastIsMulti)
        {
            var missing = fn.Params.Skip(count).Where(p => !p.IsOptional).Select(p => p.Name).ToList();
            Diag(DiagnosticSeverity.Warning, "ELU010",
                $"{fn.QualifiedName} erwartet mindestens {fn.MinArgs} Argument(e), übergeben: {count}." +
                (missing.Count > 0 ? $" Fehlend: {string.Join(", ", missing)}." : "") + $"\n{fn.Signature}", anchor);
        }
        else if (count > fn.MaxArgs)
        {
            Diag(DiagnosticSeverity.Warning, "ELU011",
                $"{fn.QualifiedName} erwartet höchstens {fn.MaxArgs} Argument(e), übergeben: {count}.\n{fn.Signature}", anchor);
        }
    }

    private void CheckCoreAvailability(ApiFunction fn, Node anchor)
    {
        if (!_ctx.Options.ReportCoreCompatibility || _ctx.TargetCore == null || fn.Source != ApiSource.Eluna || fn.Cores.Count == 0) return;
        if (!fn.Cores.Contains(_ctx.TargetCore))
        {
            Diag(DiagnosticSeverity.Warning, "ELU012",
                $"{fn.QualifiedName} ist für {_ctx.TargetCore} nicht verfügbar (nur: {string.Join(", ", fn.Cores)}).", anchor);
        }
    }

    private void CheckHookCall(CallExpr call, string registerName, ApiHook hook)
    {
        if (call.Args.Count > hook.EventArgIndex)
        {
            var evArg = call.Args[hook.EventArgIndex];
            var id = ConstantInt(evArg);
            if (id != null && hook.FindEvent(id.Value) == null)
            {
                var valid = string.Join(", ", hook.Events.Take(12).Select(e => $"{e.Id}={e.Name}"));
                var d = Diag(DiagnosticSeverity.Error, "ELU001",
                    $"Ungültige Event-ID {id} für {registerName}. Gültige IDs: {valid}{(hook.Events.Count > 12 ? ", ..." : "")}", evArg);
                var nearest = hook.Events.OrderBy(e => Math.Abs(e.Id - id.Value)).FirstOrDefault();
                if (nearest != null && evArg is LiteralExpr)
                {
                    d.Fixes.Add(new CodeFix($"Durch {nearest.Id} ({nearest.Name}) ersetzen", new TextEdit(evArg.Start, evArg.Length, nearest.Id.ToString())));
                }
            }
            else if (evArg is LiteralExpr { Kind: LiteralKind.String })
            {
                Diag(DiagnosticSeverity.Error, "ELU001", $"Die Event-ID von {registerName} muss eine Zahl sein.", evArg);
            }
        }
        if (call.Args.Count > hook.HandlerArgIndex)
        {
            var h = call.Args[hook.HandlerArgIndex];
            if (h is LiteralExpr or TableExpr)
            {
                Diag(DiagnosticSeverity.Error, "ELU003", $"{registerName} erwartet als Handler eine Funktion.", h);
            }
            else if (h is CallExpr inner && (TypeOf(inner) is { } t && t != "function" || ResolveFunction(inner.Callee) != null))
            {
                Diag(DiagnosticSeverity.Warning, "ELU003",
                    $"Der Handler wird hier aufgerufen statt übergeben – übergeben Sie die Funktion ohne '()'.", h);
            }
        }
    }

    private void CheckAioCall(CallExpr call, string member)
    {
        if (member != "Handle") return;
        // Server: AIO.Handle(player, name, handler, ...)  Client: AIO.Handle(name, handler, ...)
        var nameIndex = _model.IsClientFile ? 0 : 1;
        if (!_model.IsClientFile && call.Args.Count > 0 && call.Args[0] is LiteralExpr { Kind: LiteralKind.String })
        {
            Diag(DiagnosticSeverity.Warning, "AIO002",
                "Auf der Serverseite erwartet AIO.Handle als erstes Argument den Spieler: AIO.Handle(player, name, handlerName, ...).", call.Args[0]);
            nameIndex = 0;
        }
        if (call.Args.Count <= nameIndex || call.Args[nameIndex] is not LiteralExpr { Kind: LiteralKind.String } nameLit) return;
        var name = nameLit.StringValue ?? "";
        // AIO.Handle on the server calls handlers on the client and vice versa.
        var targetIsClient = !_model.IsClientFile;
        var reg = _ctx.AioRegistrations(name, targetIsClient);
        if (reg == null)
        {
            var side = targetIsClient ? "Client" : "Server";
            Diag(DiagnosticSeverity.Info, "AIO001",
                $"Im Projekt wurde kein {side}-seitiger AIO-Handler '{name}' gefunden (AIO.AddHandlers(\"{name}\", ...) bzw. AIO.RegisterEvent in einer {side}-Datei).", nameLit);
            return;
        }
        if (call.Args.Count > nameIndex + 1 && call.Args[nameIndex + 1] is LiteralExpr { Kind: LiteralKind.String } handlerLit && reg.HandlersKnown)
        {
            var h = handlerLit.StringValue ?? "";
            if (!reg.HandlerNames.Contains(h))
            {
                var d = Diag(DiagnosticSeverity.Warning, "AIO003", $"Der AIO-Handler '{name}' hat keine Funktion '{h}'.", handlerLit);
                foreach (var s in TextUtil.Suggest(h, reg.HandlerNames))
                {
                    var quote = handlerLit.Text.Length > 0 ? handlerLit.Text[0] : '"';
                    d.Fixes.Add(new CodeFix($"Meinten Sie '{s}'?", new TextEdit(handlerLit.Start, handlerLit.Length, quote + s + quote)));
                }
            }
        }
    }

    private void CheckMethodCall(MethodCallExpr mc)
    {
        if (mc.Method.Name.Length == 0) return;
        var objType = TypeOf(mc.Object);
        if (objType == null) return;

        if (objType.StartsWith("lib:"))
        {
            var libName = objType[4..];
            var lib = _api.FindLibrary(libName);
            if (lib != null && lib.Functions.Any(f => f.Name == mc.Method.Name))
            {
                var colon = FindSeparator(mc.Object.End, mc.Method.Start, ':');
                var d = Diag(DiagnosticSeverity.Error, "ELU002", $"{libName}.{mc.Method.Name} ist eine Bibliotheksfunktion und wird mit '.' aufgerufen.", mc.Method);
                if (colon >= 0) d.Fixes.Add(new CodeFix("':' durch '.' ersetzen", new TextEdit(colon, 1, ".")));
            }
            return;
        }

        var fn = TypeEvaluator.FindMethod(_api, objType, mc.Method.Name);
        var sym = mc.Object is NameExpr on ? Resolved(on) : null;
        if (fn == null)
        {
            if (!_api.IsKnownObjectType(objType)) return;
            var candidates = _api.GetMethodsForType(objType).Select(m => m.Name).ToList();
            var severity = sym?.TypeFromName == true ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error;
            var suggestions = TextUtil.Suggest(mc.Method.Name, candidates);
            var msg = $"{objType} hat keine Methode '{mc.Method.Name}'." + (suggestions.Count > 0 ? $" Meinten Sie '{suggestions[0]}'?" : "");
            if (sym?.TypeFromName == true) msg += $" (Typ aus Parametername '{sym.Name}' abgeleitet)";
            var d = Diag(severity, "ELU004", msg, mc.Method);
            foreach (var s in suggestions) d.Fixes.Add(new CodeFix($"Durch '{s}' ersetzen", new TextEdit(mc.Method.Start, mc.Method.Length, s)));
            return;
        }
        CheckArgumentCount(fn, mc.Args.Count, mc.Method, mc.Args);
        CheckCoreAvailability(fn, mc.Method);
    }

    private void CheckMemberAccess(MemberExpr m)
    {
        if (m.Member.Name.Length == 0) return;
        var objType = TypeOf(m.Object);
        if (objType == null || !objType.StartsWith("lib:")) return;
        var libName = objType[4..];
        var lib = _api.FindLibrary(libName);
        if (lib == null) return;
        if (lib.Functions.Any(f => f.Name == m.Member.Name) || lib.Fields.Any(f => f.Name == m.Member.Name)) return;
        if (m.Object is NameExpr n && _model.FindGlobal(n.Name)?.Fields.ContainsKey(m.Member.Name) == true) return;
        var candidates = lib.Functions.Select(f => f.Name).Concat(lib.Fields.Select(f => f.Name));
        var suggestions = TextUtil.Suggest(m.Member.Name, candidates);
        var d = Diag(DiagnosticSeverity.Warning, "ELU005",
            $"'{libName}' hat kein Feld '{m.Member.Name}'." + (suggestions.Count > 0 ? $" Meinten Sie '{suggestions[0]}'?" : ""), m.Member);
        foreach (var s in suggestions) d.Fixes.Add(new CodeFix($"Durch '{s}' ersetzen", new TextEdit(m.Member.Start, m.Member.Length, s)));
    }

    private void ReportUnresolvedGlobals()
    {
        if (!_ctx.Options.ReportUndefinedGlobals) return;
        foreach (var (n, _) in _unresolvedReads)
        {
            var name = n.Name;
            if (name.Length == 0 || name == "_" || name == "self") continue;
            if (_api.IsKnownGlobal(name)) continue;
            if (_model.GlobalDefinitions.TryGetValue(name, out var gd) && (gd.Function != null || gd.Type != null || HasWrite(name))) continue;
            if (_ctx.ProjectGlobals(name) != null) continue;

            if (ForeignNames.TryGetValue(name, out var foreign))
            {
                var fd = Diag(DiagnosticSeverity.Error, "ELU006", foreign.Message, n);
                fd.Fixes.Add(new CodeFix($"Durch '{foreign.Replacement}' ersetzen", new TextEdit(n.Start, n.Length, foreign.Replacement)));
                continue;
            }

            var severity = _model.IsClientFile ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning;
            var candidates = _model.ScopeAt(n.Start).VisibleSymbols(n.Start).Select(s => s.Name)
                .Concat(_model.GlobalDefinitions.Keys)
                .Concat(_ctx.AllProjectGlobals().Select(g => g.Name))
                .Concat(_api.Globals.Select(g => g.Name))
                .Concat(_api.Libraries.Select(l => l.Name))
                .Concat(_api.GlobalFields.Select(f => f.Name))
                .Concat(Lexer.Keywords);
            var suggestions = TextUtil.Suggest(name, candidates);
            var msg = $"Unbekannte globale Variable '{name}'." + (suggestions.Count > 0 ? $" Meinten Sie '{suggestions[0]}'?" : "");
            if (_model.IsClientFile) msg += " (Client-Datei: evtl. WoW-API)";
            var d = Diag(severity, "ELU007", msg, n);
            foreach (var s in suggestions) d.Fixes.Add(new CodeFix($"Durch '{s}' ersetzen", new TextEdit(n.Start, n.Length, s)));
        }

        if (!_ctx.Options.ReportGlobalAssignmentInFunction) return;
        foreach (var (n, stmt, inFunction) in _globalWrites)
        {
            if (!inFunction || _model.IsClientFile) continue;
            if (_ctx.ProjectGlobals(n.Name) != null || _api.IsKnownGlobal(n.Name)) continue;
            var d = Diag(DiagnosticSeverity.Hint, "ELU008",
                $"Zuweisung an die globale Variable '{n.Name}' innerhalb einer Funktion. Globale Variablen sind in Eluna für alle Skripte sichtbar – war 'local' gemeint?", n);
            if (stmt.Targets.Count == 1 && stmt.Targets[0] == n)
            {
                d.Fixes.Add(new CodeFix("Als 'local' deklarieren", new TextEdit(stmt.Start, 0, "local ")));
            }
        }
    }

    private bool HasWrite(string name) => _globalWrites.Any(w => w.Name.Name == name) ||
        _model.GlobalDefinitions.TryGetValue(name, out var g) && (g.Function != null || g.Fields.Count > 0);

    private void ReportUnusedLocals()
    {
        if (!_ctx.Options.ReportUnusedLocals) return;
        foreach (var sym in _model.AllSymbols)
        {
            if (sym.Kind is not (SymbolKind.Local or SymbolKind.LocalFunction)) continue;
            if (sym.Name.StartsWith('_') || sym.Name.Length == 0) continue;
            if (sym.ReadCount > 0) continue;
            // Locals returned at module level count as used (e.g. "return M").
            var kind = sym.Kind == SymbolKind.LocalFunction ? "Lokale Funktion" : "Lokale Variable";
            var d = Diag(DiagnosticSeverity.Hint, "ELU009", $"{kind} '{sym.Name}' wird nie verwendet.", sym.DeclarationStart, sym.Name.Length);
            d.Fixes.Add(new CodeFix($"In '_{sym.Name}' umbenennen (als absichtlich unbenutzt markieren)", new TextEdit(sym.DeclarationStart, 0, "_")));
        }
    }

    private void ReportLuaVersionFeatures()
    {
        var v = _ctx.LuaVersion;
        foreach (var f in _parse.Features)
        {
            string? msg = f.Feature switch
            {
                LuaFeature.Goto when v == LuaVersion.Lua51 => "'goto' und Labels gibt es erst ab Lua 5.2 (oder LuaJIT).",
                LuaFeature.IntegerDivision when v is LuaVersion.Lua51 or LuaVersion.Lua52 or LuaVersion.LuaJIT => "Der Operator '//' (Ganzzahldivision) gibt es erst ab Lua 5.3. Verwenden Sie math.floor(a / b).",
                LuaFeature.BitwiseOperators when v is LuaVersion.Lua51 or LuaVersion.Lua52 or LuaVersion.LuaJIT => "Bitoperatoren gibt es erst ab Lua 5.3. Verwenden Sie bit32 (5.2) bzw. bit (LuaJIT).",
                LuaFeature.Attributes when v != LuaVersion.Lua54 => "Variablen-Attribute (<const>, <close>) gibt es erst ab Lua 5.4.",
                _ => null,
            };
            if (msg != null) Diag(DiagnosticSeverity.Error, "LUA120", msg + $" (Projekt: {v.DisplayName()})", f.Start, f.Length);
        }
    }
}
