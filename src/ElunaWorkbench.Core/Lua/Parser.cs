namespace ElunaWorkbench.Core.Lua;

/// <summary>Result of parsing a Lua chunk.</summary>
public sealed class ParseResult
{
    public required Chunk Chunk { get; init; }
    public required IReadOnlyList<Token> Tokens { get; init; }
    public required List<Diagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<FeatureUse> Features { get; init; }
    public required LineIndex Lines { get; init; }
    public required string Text { get; init; }

    /// <summary>Block ranges (opening keyword offset, end offset) used for folding.</summary>
    public required IReadOnlyList<(int Start, int End)> BlockRanges { get; init; }
}

/// <summary>
/// Recursive descent parser for Lua 5.1 – 5.4 with error recovery. Never throws.
/// </summary>
public sealed class Parser
{
    private readonly string _text;
    private readonly List<Token> _tokens;
    private readonly List<Diagnostic> _diagnostics;
    private readonly List<FeatureUse> _features = new();
    private readonly List<(int Start, int End)> _blocks = new();
    private readonly List<(Token Open, Token? Close)> _blockPairs = new();
    private readonly LineIndex _lines;
    private int _index;
    private int _lastErrorPos = -1;
    private bool _missingEndReported;

    private Parser(string text)
    {
        _text = text ?? string.Empty;
        _diagnostics = new List<Diagnostic>();
        var all = Lexer.Tokenize(_text, _diagnostics);
        AllTokens = all;
        _tokens = all.Where(t => t.Kind != TokenKind.Comment).ToList();
        _lines = new LineIndex(_text);
    }

    private List<Token> AllTokens { get; }

    public static ParseResult Parse(string text)
    {
        var p = new Parser(text);
        var chunk = p.ParseChunk();
        foreach (var d in p._diagnostics)
        {
            if (d.Line == 0)
            {
                var (l, c) = p._lines.GetPosition(d.Start);
                d.Line = l;
                d.Column = c;
            }
        }
        return new ParseResult
        {
            Chunk = chunk,
            Tokens = p.AllTokens,
            Diagnostics = p._diagnostics,
            Features = p._features,
            Lines = p._lines,
            Text = p._text,
            BlockRanges = p._blocks,
        };
    }

    // -----------------------------------------------------------------------------------------
    // Token helpers
    // -----------------------------------------------------------------------------------------

    private Token Current => _tokens[Math.Min(_index, _tokens.Count - 1)];
    private Token PeekToken(int n = 1) => _tokens[Math.Min(_index + n, _tokens.Count - 1)];
    private Token Previous => _tokens[Math.Max(0, Math.Min(_index - 1, _tokens.Count - 1))];
    private bool AtEnd => Current.Kind == TokenKind.EndOfFile;

    private Token Advance()
    {
        var t = Current;
        if (_index < _tokens.Count - 1) _index++;
        return t;
    }

    private bool CheckKeyword(string kw) => Current.IsKeyword(kw);
    private bool CheckOp(string op) => Current.IsOp(op);

    private bool AcceptKeyword(string kw)
    {
        if (!CheckKeyword(kw)) return false;
        Advance();
        return true;
    }

    private bool AcceptOp(string op)
    {
        if (!CheckOp(op)) return false;
        Advance();
        return true;
    }

    private Diagnostic? Error(string message, Token at, string code = "LUA100")
    {
        return Error(message, at.Start, Math.Max(1, at.Length), code);
    }

    private Diagnostic? Error(string message, int start, int length, string code = "LUA100")
    {
        if (start <= _lastErrorPos) return null;
        _lastErrorPos = start;
        var d = new Diagnostic(DiagnosticSeverity.Error, code, message, start, length);
        _diagnostics.Add(d);
        return d;
    }

    private static string Describe(Token t) => t.Kind switch
    {
        TokenKind.EndOfFile => "Dateiende",
        TokenKind.String => "String",
        TokenKind.Number => $"Zahl '{t.Text}'",
        _ => $"'{t.Text}'",
    };

    private void ExpectOp(string op, string context)
    {
        if (AcceptOp(op)) return;
        var d = Error($"'{op}' erwartet {context}, gefunden: {Describe(Current)}.", ErrorAnchor());
        d?.Fixes.Add(new CodeFix($"'{op}' einfügen", new TextEdit(Previous.End, 0, op)));
    }

    private void ExpectKeyword(string kw, string context)
    {
        if (AcceptKeyword(kw)) return;
        var d = Error($"'{kw}' erwartet {context}, gefunden: {Describe(Current)}.", ErrorAnchor());
        d?.Fixes.Add(new CodeFix($"'{kw}' einfügen", new TextEdit(Previous.End, 0, " " + kw)));
    }

    /// <summary>Token used to place an error: the current token, or the previous one at EOF.</summary>
    private Token ErrorAnchor() => AtEnd && _index > 0 ? Previous : Current;

    private Identifier ExpectName(string context)
    {
        var t = Current;
        if (t.Kind == TokenKind.Identifier)
        {
            Advance();
            return new Identifier(t.Text, t.Start, t.Length);
        }
        if (t.Kind == TokenKind.Keyword && !IsBlockKeyword(t.Text))
        {
            Error($"'{t.Text}' ist ein reserviertes Schlüsselwort und kann nicht als Name verwendet werden.", t, "LUA101");
            Advance();
            return new Identifier(t.Text, t.Start, t.Length);
        }
        Error($"Name erwartet {context}, gefunden: {Describe(t)}.", ErrorAnchor(), "LUA101");
        return new Identifier("", t.Start, 0);
    }

    private static bool IsBlockKeyword(string kw) => kw is "end" or "else" or "elseif" or "until" or "then" or "do";

    private void ExpectEnd(Token opener, string what)
    {
        if (CheckKeyword("end"))
        {
            var endTok = Advance();
            _blocks.Add((opener.Start, endTok.End));
            _blockPairs.Add((opener, endTok));
            return;
        }
        _blockPairs.Add((opener, null));
        ReportMissingEnd(opener, what);
    }

    private void ReportMissingEnd(Token opener, string what)
    {
        if (_missingEndReported && AtEnd) return;
        var (line, _) = _lines.GetPosition(opener.Start);
        var culprit = AtEnd ? FindLikelyUnclosedBlock() : null;
        var target = culprit ?? opener;
        var (targetLine, _) = _lines.GetPosition(target.Start);
        var insertAt = FindEndInsertionPoint(target);
        var indent = GetIndent(targetLine);
        var message = culprit == null
            ? $"'end' erwartet (um '{what}' in Zeile {line} zu schließen), gefunden: {Describe(Current)}."
            : $"'end' fehlt für '{target.Text}' in Zeile {targetLine}.";
        var anchor = culprit ?? ErrorAnchor();
        var prevLast = _lastErrorPos;
        if (culprit != null) _lastErrorPos = -1; // always report the root cause
        var d = Error(message, anchor.Start, anchor.Length, "LUA102");
        if (d == null) _lastErrorPos = prevLast;
        if (d != null)
        {
            var newText = insertAt >= _text.Length
                ? (_text.EndsWith('\n') ? "" : "\n") + indent + "end\n"
                : indent + "end\n";
            d.Fixes.Add(new CodeFix($"Fehlendes 'end' für '{target.Text}' (Zeile {targetLine}) einfügen", new TextEdit(insertAt, 0, newText)));
        }
        if (AtEnd) _missingEndReported = true;
    }

    /// <summary>
    /// When a block is not closed at end of file, the 'end' usually is missing in an inner
    /// block which "stole" the end of its parent. Detect this by comparing indentation.
    /// </summary>
    private Token? FindLikelyUnclosedBlock()
    {
        foreach (var (open, close) in _blockPairs.OrderBy(p => p.Open.Start))
        {
            if (close == null) continue;
            var openLine = _lines.GetPosition(open.Start).Line;
            var closeLine = _lines.GetPosition(close.Value.Start).Line;
            if (openLine == closeLine) continue;
            if (GetIndent(closeLine).Length < GetIndent(openLine).Length) return OpenerKeyword(open);
        }
        return null;
    }

    private Token OpenerKeyword(Token t) => t;

    private int FindEndInsertionPoint(Token opener)
    {
        var (line, _) = _lines.GetPosition(opener.Start);
        var indent = GetIndent(line).Length;
        for (var l = line + 1; l <= _lines.LineCount; l++)
        {
            var content = GetLineText(l);
            if (content.Trim().Length == 0) continue;
            var ind = content.Length - content.TrimStart().Length;
            if (ind <= indent)
            {
                var trimmed = content.TrimStart();
                if (trimmed.StartsWith("else") || trimmed.StartsWith("until")) continue;
                return _lines.GetLineStart(l);
            }
        }
        return _text.Length;
    }

    private string GetLineText(int line)
    {
        var start = _lines.GetLineStart(line);
        var end = line < _lines.LineCount ? _lines.GetLineStart(line + 1) : _text.Length;
        return _text.Substring(start, end - start).TrimEnd('\r', '\n');
    }

    private string GetIndent(int line)
    {
        var t = GetLineText(line);
        return t.Substring(0, t.Length - t.TrimStart().Length);
    }

    // -----------------------------------------------------------------------------------------
    // Grammar
    // -----------------------------------------------------------------------------------------

    private Chunk ParseChunk()
    {
        var chunk = new Chunk { Start = 0 };
        chunk.Body = ParseBlock();
        while (!AtEnd)
        {
            var t = Current;
            var d = Error($"Unerwartetes {Describe(t)} – kein offener Block, der hier geschlossen werden könnte.", t, "LUA103");
            if (d != null && t.IsKeyword("end"))
            {
                d.Fixes.Add(new CodeFix("Überflüssiges 'end' entfernen", new TextEdit(t.Start, t.Length, "")));
            }
            Advance();
            var more = ParseBlock();
            chunk.Body.Statements.AddRange(more.Statements);
        }
        chunk.End = _text.Length;
        chunk.Body.End = _text.Length;
        return chunk;
    }

    private bool IsBlockEnd() =>
        AtEnd || CheckKeyword("end") || CheckKeyword("else") || CheckKeyword("elseif") || CheckKeyword("until");

    private Block ParseBlock()
    {
        var block = new Block { Start = Current.Start };
        while (!IsBlockEnd())
        {
            if (CheckKeyword("return"))
            {
                block.Statements.Add(ParseReturn());
                if (!IsBlockEnd())
                {
                    var d = Error($"Nach 'return' muss der Block enden ('end' erwartet), gefunden: {Describe(Current)}.", Current, "LUA104");
                    continue;
                }
                break;
            }
            var before = _index;
            var stmt = ParseStatement();
            if (stmt != null) block.Statements.Add(stmt);
            if (_index == before)
            {
                // No progress – skip the offending token to avoid endless loops.
                Error($"Unerwartetes {Describe(Current)}.", Current);
                Advance();
            }
        }
        block.End = Previous.End;
        return block;
    }

    private Stmt? ParseStatement()
    {
        var t = Current;
        if (t.Kind == TokenKind.Operator)
        {
            if (t.Text == ";")
            {
                Advance();
                return null;
            }
            if (t.Text == "::")
            {
                return ParseLabel();
            }
        }
        if (t.Kind == TokenKind.Keyword)
        {
            switch (t.Text)
            {
                case "if": return ParseIf();
                case "while": return ParseWhile();
                case "do":
                {
                    Advance();
                    var s = new DoStmt { Start = t.Start, Body = ParseBlock() };
                    ExpectEnd(t, "do");
                    s.End = Previous.End;
                    return s;
                }
                case "for": return ParseFor();
                case "repeat": return ParseRepeat();
                case "function": return ParseFunctionStmt();
                case "local":
                    Advance();
                    if (CheckKeyword("function")) return ParseLocalFunction(t);
                    return ParseLocal(t);
                case "break":
                    Advance();
                    return new BreakStmt { Start = t.Start, End = t.End };
                case "goto":
                    if (PeekToken().Kind == TokenKind.Identifier)
                    {
                        Advance();
                        _features.Add(new FeatureUse(LuaFeature.Goto, t.Start, t.Length));
                        var label = ExpectName("nach 'goto'");
                        return new GotoStmt { Start = t.Start, End = label.End, Label = label };
                    }
                    break;
                case "then":
                case "in":
                    break;
            }
        }
        return ParseExprStatement();
    }

    private Stmt ParseLabel()
    {
        var start = Advance();
        _features.Add(new FeatureUse(LuaFeature.Goto, start.Start, start.Length));
        var name = ExpectName("für ein Label");
        ExpectOp("::", "nach dem Label-Namen");
        return new LabelStmt { Start = start.Start, End = Previous.End, Label = name };
    }

    private Stmt ParseIf()
    {
        var ifTok = Advance();
        var stmt = new IfStmt { Start = ifTok.Start };
        var cond = ParseExpr();
        ExpectThen(ifTok);
        var clause = new IfClause { Start = ifTok.Start, Condition = cond };
        clause.Body = ParseBlock();
        clause.End = Previous.End;
        stmt.Clauses.Add(clause);
        while (CheckKeyword("elseif"))
        {
            var ei = Advance();
            var c = ParseExpr();
            ExpectThen(ei);
            var cl = new IfClause { Start = ei.Start, Condition = c };
            cl.Body = ParseBlock();
            cl.End = Previous.End;
            stmt.Clauses.Add(cl);
        }
        if (CheckKeyword("else"))
        {
            var elseTok = Advance();
            // Hint for "else if" (other languages) which needs an additional 'end' in Lua.
            if (CheckKeyword("if") && _lines.GetPosition(Current.Start).Line == _lines.GetPosition(elseTok.Start).Line)
            {
                var ifTok2 = Current;
                var d = new Diagnostic(DiagnosticSeverity.Warning, "LUA110",
                    "'else if' öffnet einen neuen Block und benötigt ein zusätzliches 'end'. Meinten Sie 'elseif'?",
                    elseTok.Start, ifTok2.End - elseTok.Start);
                d.Fixes.Add(new CodeFix("Durch 'elseif' ersetzen", new TextEdit(elseTok.Start, ifTok2.End - elseTok.Start, "elseif")));
                _diagnostics.Add(d);
            }
            stmt.Else = ParseBlock();
        }
        ExpectEnd(ifTok, "if");
        stmt.End = Previous.End;
        return stmt;
    }

    private void ExpectThen(Token opener)
    {
        if (AcceptKeyword("then")) return;
        if (CheckOp("{") || CheckOp(":"))
        {
            var t = Current;
            var d = Error($"'then' erwartet nach der Bedingung, gefunden: {Describe(t)}.", t);
            d?.Fixes.Add(new CodeFix("Durch 'then' ersetzen", new TextEdit(t.Start, t.Length, "then")));
            Advance();
            return;
        }
        var e = Error($"'then' erwartet nach der Bedingung von '{opener.Text}', gefunden: {Describe(Current)}.", ErrorAnchor());
        e?.Fixes.Add(new CodeFix("'then' einfügen", new TextEdit(Previous.End, 0, " then")));
    }

    private Stmt ParseWhile()
    {
        var w = Advance();
        var cond = ParseExpr();
        ExpectDo(w);
        var stmt = new WhileStmt { Start = w.Start, Condition = cond };
        stmt.Body = ParseBlock();
        ExpectEnd(w, "while");
        stmt.End = Previous.End;
        return stmt;
    }

    private void ExpectDo(Token opener)
    {
        if (AcceptKeyword("do")) return;
        if (CheckOp("{"))
        {
            var t = Current;
            var d = Error($"'do' erwartet, gefunden: {Describe(t)}.", t);
            d?.Fixes.Add(new CodeFix("Durch 'do' ersetzen", new TextEdit(t.Start, t.Length, "do")));
            Advance();
            return;
        }
        var e = Error($"'do' erwartet nach '{opener.Text}', gefunden: {Describe(Current)}.", ErrorAnchor());
        e?.Fixes.Add(new CodeFix("'do' einfügen", new TextEdit(Previous.End, 0, " do")));
    }

    private Stmt ParseRepeat()
    {
        var r = Advance();
        var stmt = new RepeatStmt { Start = r.Start };
        stmt.Body = ParseBlock();
        if (AcceptKeyword("until"))
        {
            _blocks.Add((r.Start, Previous.End));
            stmt.Condition = ParseExpr();
        }
        else
        {
            var (line, _) = _lines.GetPosition(r.Start);
            Error($"'until' erwartet (um 'repeat' in Zeile {line} zu schließen), gefunden: {Describe(Current)}.", ErrorAnchor(), "LUA102");
        }
        stmt.End = Previous.End;
        return stmt;
    }

    private Stmt ParseFor()
    {
        var f = Advance();
        var first = ExpectName("nach 'for'");
        if (CheckOp("="))
        {
            Advance();
            var from = ParseExpr();
            ExpectOp(",", "in der numerischen for-Schleife");
            var to = ParseExpr();
            Expr? step = null;
            if (AcceptOp(",")) step = ParseExpr();
            ExpectDo(f);
            var stmt = new NumericForStmt { Start = f.Start, Variable = first, From = from, To = to, Step = step };
            stmt.Body = ParseBlock();
            ExpectEnd(f, "for");
            stmt.End = Previous.End;
            return stmt;
        }
        var g = new GenericForStmt { Start = f.Start };
        g.Names.Add(first);
        while (AcceptOp(",")) g.Names.Add(ExpectName("in der for-Schleife"));
        ExpectKeyword("in", "in der for-Schleife");
        g.Iterators.AddRange(ParseExprList());
        ExpectDo(f);
        g.Body = ParseBlock();
        ExpectEnd(f, "for");
        g.End = Previous.End;
        return g;
    }

    private Stmt ParseFunctionStmt()
    {
        var fnTok = Advance();
        var firstName = ExpectName("nach 'function'");
        Expr target = new NameExpr(firstName);
        Identifier? method = null;
        var display = firstName.Name;
        while (CheckOp("."))
        {
            Advance();
            var member = ExpectName("nach '.'");
            target = new MemberExpr { Object = target, Member = member, Start = target.Start, End = member.End };
            display += "." + member.Name;
        }
        if (AcceptOp(":"))
        {
            method = ExpectName("nach ':'");
            display += ":" + method.Name;
        }
        var func = ParseFunctionBody(fnTok, method != null);
        func.DisplayName = display;
        return new FunctionStmt { Start = fnTok.Start, End = func.End, Target = target, MethodName = method, Function = func };
    }

    private Stmt ParseLocalFunction(Token localTok)
    {
        var fnTok = Advance();
        var name = ExpectName("nach 'local function'");
        var func = ParseFunctionBody(fnTok, false);
        func.DisplayName = name.Name;
        return new LocalFunctionStmt { Start = localTok.Start, End = func.End, Name = name, Function = func };
    }

    private Stmt ParseLocal(Token localTok)
    {
        var stmt = new LocalStmt { Start = localTok.Start };
        do
        {
            var name = ExpectName("nach 'local'");
            if (CheckOp("<") && PeekToken().Kind == TokenKind.Identifier && PeekToken(2).IsOp(">"))
            {
                var lt = Advance();
                var attrib = Advance();
                Advance();
                name.Attribute = attrib.Text;
                _features.Add(new FeatureUse(LuaFeature.Attributes, lt.Start, Previous.End - lt.Start));
                if (attrib.Text != "const" && attrib.Text != "close")
                {
                    Error($"Unbekanntes Attribut '<{attrib.Text}>' (erlaubt: const, close).", attrib);
                }
            }
            stmt.Names.Add(name);
        } while (AcceptOp(","));
        if (AcceptOp("="))
        {
            stmt.Values.AddRange(ParseExprList());
        }
        else if (IsCompoundAssignOp(Current))
        {
            ReportCompoundAssign(stmt.Names.Count == 1 ? stmt.Names[0].Name : null);
            ParseExprList();
        }
        stmt.End = Previous.End;
        return stmt;
    }

    private static bool IsCompoundAssignOp(Token t) => t.Kind == TokenKind.Operator && t.Text is "+=" or "-=" or "*=" or "/=";

    private void ReportCompoundAssign(string? targetText)
    {
        var op = Advance();
        var d = Error($"Lua unterstützt den Operator '{op.Text}' nicht. Schreiben Sie 'x = x {op.Text[0]} ...'.", op, "LUA111");
        if (d != null && targetText != null)
        {
            d.Fixes.Add(new CodeFix($"In '= {targetText} {op.Text[0]}' umwandeln", new TextEdit(op.Start, op.Length, $"= {targetText} {op.Text[0]}")));
        }
    }

    private Stmt ParseReturn()
    {
        var r = Advance();
        var stmt = new ReturnStmt { Start = r.Start };
        if (!IsBlockEnd() && !CheckOp(";"))
        {
            stmt.Values.AddRange(ParseExprList());
        }
        AcceptOp(";");
        stmt.End = Previous.End;
        return stmt;
    }

    private Stmt? ParseExprStatement()
    {
        var startTok = Current;
        var expr = ParseSuffixedExpr();
        if (expr is ErrorExpr) return null;

        if (CheckOp("=") || CheckOp(","))
        {
            var stmt = new AssignStmt { Start = startTok.Start };
            stmt.Targets.Add(expr);
            CheckAssignable(expr);
            while (AcceptOp(","))
            {
                var t = ParseSuffixedExpr();
                CheckAssignable(t);
                stmt.Targets.Add(t);
            }
            ExpectOp("=", "in der Zuweisung");
            stmt.Values.AddRange(ParseExprList());
            stmt.End = Previous.End;
            return stmt;
        }
        if (IsCompoundAssignOp(Current))
        {
            var targetText = _text.Substring(expr.Start, expr.End - expr.Start);
            ReportCompoundAssign(targetText);
            var stmt = new AssignStmt { Start = startTok.Start };
            stmt.Targets.Add(expr);
            stmt.Values.AddRange(ParseExprList());
            stmt.End = Previous.End;
            return stmt;
        }
        if (CheckOp("++") || (CheckOp("-") && PeekToken().IsOp("-") && PeekToken().Start == Current.End))
        {
            var op = Advance();
            var targetText = _text.Substring(expr.Start, expr.End - expr.Start);
            var d = Error($"Lua kennt keinen Operator '{op.Text}'. Schreiben Sie '{targetText} = {targetText} + 1'.", op, "LUA111");
            d?.Fixes.Add(new CodeFix($"In '{targetText} = {targetText} + 1' umwandeln", new TextEdit(op.Start, op.Length, $" = {targetText} + 1")));
            return new AssignStmt { Start = startTok.Start, End = Previous.End };
        }
        if (expr is CallExpr or MethodCallExpr)
        {
            return new CallStmt { Start = expr.Start, End = expr.End, Call = expr };
        }

        var anchor = Current;
        if (expr is NameExpr ne && ne.Name is "elif" or "elsif")
        {
            var d = Error($"'{ne.Name}' ist kein Lua-Schlüsselwort. Meinten Sie 'elseif'?", ne.Start, ne.Length, "LUA112");
            d?.Fixes.Add(new CodeFix("Durch 'elseif' ersetzen", new TextEdit(ne.Start, ne.Length, "elseif")));
            return null;
        }
        Error($"Syntaxfehler: Ausdruck kann nicht als Anweisung verwendet werden (Zuweisung oder Funktionsaufruf erwartet) bei {Describe(anchor)}.",
            expr.Start, Math.Max(1, expr.Length), "LUA105");
        return null;
    }

    private void CheckAssignable(Expr e)
    {
        if (e is NameExpr or MemberExpr or IndexExpr) return;
        Error("Diesem Ausdruck kann nichts zugewiesen werden.", e.Start, Math.Max(1, e.Length), "LUA106");
    }

    // -----------------------------------------------------------------------------------------
    // Expressions
    // -----------------------------------------------------------------------------------------

    private List<Expr> ParseExprList()
    {
        var list = new List<Expr> { ParseExpr() };
        while (AcceptOp(",")) list.Add(ParseExpr());
        return list;
    }

    private static (int Left, int Right)? BinaryPriority(Token t)
    {
        if (t.Kind == TokenKind.Keyword)
        {
            return t.Text switch
            {
                "or" => (1, 1),
                "and" => (2, 2),
                _ => null,
            };
        }
        if (t.Kind != TokenKind.Operator) return null;
        return t.Text switch
        {
            "||" => (1, 1),
            "&&" => (2, 2),
            "<" or ">" or "<=" or ">=" or "~=" or "==" or "!=" => (3, 3),
            "|" => (4, 4),
            "~" => (5, 5),
            "&" => (6, 6),
            "<<" or ">>" => (7, 7),
            ".." => (9, 8),
            "+" or "-" => (10, 10),
            "*" or "/" or "//" or "%" => (11, 11),
            "^" => (14, 13),
            _ => null,
        };
    }

    private const int UnaryPriority = 12;

    public Expr ParseExpr(int limit = 0)
    {
        Expr left;
        var t = Current;
        if (t.IsKeyword("not") || t.IsOp("-") || t.IsOp("#") || t.IsOp("~") || t.IsOp("!"))
        {
            Advance();
            var op = t.Text;
            if (op == "!")
            {
                var d = Error("Lua verwendet 'not' statt '!'.", t, "LUA111");
                d?.Fixes.Add(new CodeFix("Durch 'not ' ersetzen", new TextEdit(t.Start, t.Length, "not ")));
                op = "not";
            }
            else if (op == "~")
            {
                _features.Add(new FeatureUse(LuaFeature.BitwiseOperators, t.Start, t.Length));
            }
            var operand = ParseExpr(UnaryPriority);
            left = new UnaryExpr { Operator = op, Operand = operand, Start = t.Start, End = operand.End };
        }
        else
        {
            left = ParseSimpleExpr();
        }

        while (true)
        {
            var opTok = Current;
            var prio = BinaryPriority(opTok);
            if (prio == null || prio.Value.Left <= limit) break;
            Advance();
            var op = opTok.Text;
            switch (op)
            {
                case "!=":
                {
                    var d = Error("Lua verwendet '~=' für Ungleichheit, nicht '!='.", opTok, "LUA111");
                    d?.Fixes.Add(new CodeFix("Durch '~=' ersetzen", new TextEdit(opTok.Start, opTok.Length, "~=")));
                    op = "~=";
                    break;
                }
                case "&&":
                {
                    var d = Error("Lua verwendet 'and' statt '&&'.", opTok, "LUA111");
                    d?.Fixes.Add(new CodeFix("Durch 'and' ersetzen", new TextEdit(opTok.Start, opTok.Length, "and")));
                    op = "and";
                    break;
                }
                case "||":
                {
                    var d = Error("Lua verwendet 'or' statt '||'.", opTok, "LUA111");
                    d?.Fixes.Add(new CodeFix("Durch 'or' ersetzen", new TextEdit(opTok.Start, opTok.Length, "or")));
                    op = "or";
                    break;
                }
                case "//":
                    _features.Add(new FeatureUse(LuaFeature.IntegerDivision, opTok.Start, opTok.Length));
                    break;
                case "&":
                case "|":
                case "~":
                case "<<":
                case ">>":
                    _features.Add(new FeatureUse(LuaFeature.BitwiseOperators, opTok.Start, opTok.Length));
                    break;
            }
            var right = ParseExpr(prio.Value.Right);
            left = new BinaryExpr { Operator = op, Left = left, Right = right, Start = left.Start, End = right.End };
        }
        return left;
    }

    private Expr ParseSimpleExpr()
    {
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.Number:
                Advance();
                return new LiteralExpr { Kind = LiteralKind.Number, Text = t.Text, Start = t.Start, End = t.End };
            case TokenKind.String:
                Advance();
                return new LiteralExpr { Kind = LiteralKind.String, Text = t.Text, StringValue = t.Value, Start = t.Start, End = t.End };
            case TokenKind.Keyword:
                switch (t.Text)
                {
                    case "nil":
                        Advance();
                        return new LiteralExpr { Kind = LiteralKind.Nil, Text = t.Text, Start = t.Start, End = t.End };
                    case "true":
                        Advance();
                        return new LiteralExpr { Kind = LiteralKind.True, Text = t.Text, Start = t.Start, End = t.End };
                    case "false":
                        Advance();
                        return new LiteralExpr { Kind = LiteralKind.False, Text = t.Text, Start = t.Start, End = t.End };
                    case "function":
                        Advance();
                        return ParseFunctionBody(t, false);
                }
                break;
            case TokenKind.Operator:
                if (t.Text == "...")
                {
                    Advance();
                    return new LiteralExpr { Kind = LiteralKind.VarArgs, Text = t.Text, Start = t.Start, End = t.End };
                }
                if (t.Text == "{") return ParseTable();
                break;
        }
        return ParseSuffixedExpr();
    }

    private Expr ParsePrimaryExpr()
    {
        var t = Current;
        if (t.Kind == TokenKind.Identifier)
        {
            Advance();
            return new NameExpr(new Identifier(t.Text, t.Start, t.Length));
        }
        if (t.IsOp("("))
        {
            Advance();
            var inner = ParseExpr();
            ExpectOp(")", "um den geklammerten Ausdruck zu schließen");
            return new ParenExpr { Inner = inner, Start = t.Start, End = Previous.End };
        }
        if (t.Kind == TokenKind.Keyword && t.Text is "nil" or "true" or "false" or "function" || t.Kind is TokenKind.Number or TokenKind.String || t.IsOp("{") || t.IsOp("..."))
        {
            return ParseSimpleExpr();
        }
        Error($"Ausdruck erwartet, gefunden: {Describe(t)}.", ErrorAnchor(), "LUA107");
        return new ErrorExpr { Start = t.Start, End = t.Start };
    }

    private Expr ParseSuffixedExpr()
    {
        var expr = ParsePrimaryExpr();
        if (expr is ErrorExpr) return expr;
        while (true)
        {
            var t = Current;
            if (t.IsOp("."))
            {
                Advance();
                var name = ExpectName("nach '.'");
                expr = new MemberExpr { Object = expr, Member = name, Start = expr.Start, End = Math.Max(name.End, t.End) };
            }
            else if (t.IsOp("["))
            {
                // A '[' on a new line after a complete expression is ambiguous but valid Lua.
                Advance();
                var key = ParseExpr();
                ExpectOp("]", "um den Index zu schließen");
                expr = new IndexExpr { Object = expr, Key = key, Start = expr.Start, End = Previous.End };
            }
            else if (t.IsOp(":"))
            {
                Advance();
                var name = ExpectName("nach ':' (Methodenname)");
                var call = new MethodCallExpr { Object = expr, Method = name, Start = expr.Start };
                if (CheckOp("(") || CheckOp("{") || Current.Kind == TokenKind.String)
                {
                    call.ArgsStart = Current.Start;
                    ParseCallArgs(call.Args);
                }
                else
                {
                    var d = Error($"Argumentliste '(' erwartet nach Methodenaufruf ':{name.Name}'.", ErrorAnchor(), "LUA108");
                    d?.Fixes.Add(new CodeFix("'()' einfügen", new TextEdit(Previous.End, 0, "()")));
                    call.ArgsStart = Previous.End;
                }
                call.End = Previous.End;
                expr = call;
            }
            else if (t.IsOp("(") || t.IsOp("{") || t.Kind == TokenKind.String)
            {
                var call = new CallExpr { Callee = expr, Start = expr.Start, ArgsStart = t.Start };
                ParseCallArgs(call.Args);
                call.End = Previous.End;
                expr = call;
            }
            else
            {
                return expr;
            }
        }
    }

    private void ParseCallArgs(List<Expr> args)
    {
        var t = Current;
        if (t.Kind == TokenKind.String)
        {
            Advance();
            args.Add(new LiteralExpr { Kind = LiteralKind.String, Text = t.Text, StringValue = t.Value, Start = t.Start, End = t.End });
            return;
        }
        if (t.IsOp("{"))
        {
            args.Add(ParseTable());
            return;
        }
        Advance(); // (
        if (!CheckOp(")"))
        {
            args.AddRange(ParseExprList());
        }
        if (!AcceptOp(")"))
        {
            var d = Error($"')' erwartet um die Argumentliste zu schließen, gefunden: {Describe(Current)}.", ErrorAnchor(), "LUA109");
            d?.Fixes.Add(new CodeFix("')' einfügen", new TextEdit(Previous.End, 0, ")")));
        }
    }

    private FunctionExpr ParseFunctionBody(Token fnTok, bool isMethod)
    {
        var func = new FunctionExpr { Start = fnTok.Start, HasImplicitSelf = isMethod };
        ExpectOp("(", "für die Parameterliste");
        if (!CheckOp(")"))
        {
            do
            {
                if (CheckOp("..."))
                {
                    Advance();
                    func.IsVarArg = true;
                    break;
                }
                var p = ExpectName("in der Parameterliste");
                if (p.Name.Length == 0) break;
                func.Params.Add(p);
            } while (AcceptOp(","));
        }
        if (!AcceptOp(")"))
        {
            var d = Error($"')' erwartet um die Parameterliste zu schließen, gefunden: {Describe(Current)}.", ErrorAnchor(), "LUA109");
            d?.Fixes.Add(new CodeFix("')' einfügen", new TextEdit(Previous.End, 0, ")")));
        }
        func.Body = ParseBlock();
        ExpectEnd(fnTok, "function");
        func.End = Previous.End;
        return func;
    }

    private Expr ParseTable()
    {
        var open = Advance(); // {
        var table = new TableExpr { Start = open.Start };
        while (!CheckOp("}") && !AtEnd)
        {
            var fieldStart = Current.Start;
            if (CheckOp("["))
            {
                Advance();
                var key = ParseExpr();
                ExpectOp("]", "um den Tabellenschlüssel zu schließen");
                ExpectOp("=", "nach dem Tabellenschlüssel");
                var value = ParseExpr();
                table.Fields.Add(new TableField { Key = key, Value = value, Start = fieldStart, End = value.End });
            }
            else if (Current.Kind == TokenKind.Identifier && PeekToken().IsOp("="))
            {
                var nameTok = Advance();
                Advance(); // =
                var value = ParseExpr();
                table.Fields.Add(new TableField { Name = new Identifier(nameTok.Text, nameTok.Start, nameTok.Length), Value = value, Start = fieldStart, End = value.End });
            }
            else
            {
                var before = _index;
                var value = ParseExpr();
                table.Fields.Add(new TableField { Value = value, Start = fieldStart, End = value.End });
                if (_index == before) break;
            }
            if (!AcceptOp(",") && !AcceptOp(";"))
            {
                if (!CheckOp("}"))
                {
                    var d = Error($"',' oder '}}' erwartet im Tabellenkonstruktor, gefunden: {Describe(Current)}.", ErrorAnchor(), "LUA109");
                    d?.Fixes.Add(new CodeFix("',' einfügen", new TextEdit(Previous.End, 0, ",")));
                    if (Current.Kind is TokenKind.Identifier or TokenKind.String or TokenKind.Number || CheckOp("[") || CheckOp("{"))
                    {
                        continue;
                    }
                }
                break;
            }
        }
        if (!AcceptOp("}"))
        {
            var d = Error($"'}}' erwartet um die Tabelle zu schließen, gefunden: {Describe(Current)}.", ErrorAnchor(), "LUA109");
            d?.Fixes.Add(new CodeFix("'}' einfügen", new TextEdit(Previous.End, 0, "}")));
        }
        table.End = Previous.End;
        if (_lines.GetPosition(open.Start).Line != _lines.GetPosition(table.End).Line) _blocks.Add((open.Start, table.End));
        return table;
    }
}
