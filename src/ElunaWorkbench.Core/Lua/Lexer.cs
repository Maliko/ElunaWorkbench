using System.Text;

namespace ElunaWorkbench.Core.Lua;

public enum TokenKind
{
    Identifier,
    Keyword,
    Number,
    String,
    Comment,
    Operator,
    Unknown,
    EndOfFile,
}

public readonly record struct Token(TokenKind Kind, string Text, int Start, int Length, int Line, int Column)
{
    public int End => Start + Length;

    /// <summary>For strings: the unescaped value (best effort). For other tokens: the text.</summary>
    public string? Value { get; init; }

    public bool Is(TokenKind kind, string text) => Kind == kind && Text == text;
    public bool IsKeyword(string kw) => Kind == TokenKind.Keyword && Text == kw;
    public bool IsOp(string op) => Kind == TokenKind.Operator && Text == op;

    public override string ToString() => $"{Kind} '{Text}' @{Line}:{Column}";
}

/// <summary>
/// A tolerant Lua lexer. It never throws; problems are reported as diagnostics.
/// Supports Lua 5.1 – 5.4 and LuaJIT syntax (long strings/comments, all escapes,
/// hex floats, integer division, bitwise operators, goto labels, attribs).
/// </summary>
public sealed class Lexer
{
    public static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
        "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    };

    // Longest operators first.
    private static readonly string[] Operators =
    {
        "...", "..", "==", "~=", "<=", ">=", "<<", ">>", "//", "::",
        // operators borrowed from other languages – lexed so the parser can give good hints
        "!=", "&&", "||", "+=", "-=", "*=", "/=", "++",
        "+", "-", "*", "/", "%", "^", "#", "&", "~", "|", "<", ">", "=", "(", ")", "{", "}", "[", "]",
        ";", ":", ",", ".", "!",
    };

    private readonly string _text;
    private readonly List<Diagnostic> _diagnostics;
    private int _pos;
    private int _line = 1;
    private int _lineStart;

    public Lexer(string text, List<Diagnostic>? diagnostics = null)
    {
        _text = text ?? string.Empty;
        _diagnostics = diagnostics ?? new List<Diagnostic>();
    }

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>Tokenizes the whole text, including comments.</summary>
    public static List<Token> Tokenize(string text, List<Diagnostic>? diagnostics = null)
    {
        var lexer = new Lexer(text, diagnostics);
        var tokens = new List<Token>();
        while (true)
        {
            var t = lexer.Next();
            tokens.Add(t);
            if (t.Kind == TokenKind.EndOfFile) break;
        }
        return tokens;
    }

    public Token Next()
    {
        SkipWhitespace();
        if (_pos >= _text.Length)
        {
            return new Token(TokenKind.EndOfFile, "", _text.Length, 0, _line, _pos - _lineStart + 1);
        }

        var start = _pos;
        var line = _line;
        var col = _pos - _lineStart + 1;
        var c = _text[_pos];

        // Shebang on first line
        if (start == 0 && c == '#' && Peek(1) == '!')
        {
            while (_pos < _text.Length && _text[_pos] != '\n') _pos++;
            return Make(TokenKind.Comment, start, line, col);
        }

        if (c == '-' && Peek(1) == '-')
        {
            _pos += 2;
            if (Peek(0) == '[')
            {
                var level = LongBracketLevel(_pos);
                if (level >= 0)
                {
                    ReadLongBracket(level, start, line, col, "Kommentar");
                    return Make(TokenKind.Comment, start, line, col);
                }
            }
            while (_pos < _text.Length && _text[_pos] != '\n' && _text[_pos] != '\r') _pos++;
            return Make(TokenKind.Comment, start, line, col);
        }

        if (IsIdentStart(c))
        {
            while (_pos < _text.Length && IsIdentPart(_text[_pos])) _pos++;
            var word = _text.Substring(start, _pos - start);
            return new Token(Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Identifier, word, start, _pos - start, line, col);
        }

        if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(1))))
        {
            ReadNumber(start, line, col);
            return Make(TokenKind.Number, start, line, col);
        }

        if (c == '"' || c == '\'')
        {
            var value = ReadQuotedString(c, start, line, col);
            return Make(TokenKind.String, start, line, col) with { Value = value };
        }

        if (c == '[')
        {
            var level = LongBracketLevel(_pos);
            if (level >= 0)
            {
                var contentStart = _pos + level + 2;
                ReadLongBracket(level, start, line, col, "String");
                var contentEnd = Math.Max(contentStart, _pos - level - 2);
                var value = contentEnd <= _text.Length && contentEnd >= contentStart ? _text.Substring(contentStart, contentEnd - contentStart) : "";
                if (value.StartsWith("\r\n")) value = value[2..];
                else if (value.StartsWith('\n')) value = value[1..];
                return Make(TokenKind.String, start, line, col) with { Value = value };
            }
        }

        foreach (var op in Operators)
        {
            if (string.CompareOrdinal(_text, _pos, op, 0, op.Length) == 0)
            {
                _pos += op.Length;
                return new Token(TokenKind.Operator, op, start, op.Length, line, col);
            }
        }

        _pos++;
        var bad = new Token(TokenKind.Unknown, c.ToString(), start, 1, line, col);
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "LUA001", $"Unerwartetes Zeichen '{c}'.", start, 1, line, col));
        return bad;
    }

    private Token Make(TokenKind kind, int start, int line, int col) =>
        new(kind, _text.Substring(start, _pos - start), start, _pos - start, line, col);

    private char Peek(int offset) => _pos + offset < _text.Length ? _text[_pos + offset] : '\0';

    private void SkipWhitespace()
    {
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c == '\n')
            {
                _pos++;
                NewLine();
            }
            else if (c == '\r')
            {
                _pos++;
                if (_pos < _text.Length && _text[_pos] == '\n') _pos++;
                NewLine();
            }
            else if (char.IsWhiteSpace(c))
            {
                _pos++;
            }
            else
            {
                break;
            }
        }
    }

    private void NewLine()
    {
        _line++;
        _lineStart = _pos;
    }

    private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_' || c > 127 && char.IsLetter(c);
    private static bool IsIdentPart(char c) => IsIdentStart(c) || char.IsAsciiDigit(c);

    /// <summary>Returns the level of a long bracket starting at pos ("[==[" => 2), or -1.</summary>
    private int LongBracketLevel(int pos)
    {
        if (pos >= _text.Length || _text[pos] != '[') return -1;
        var i = pos + 1;
        var level = 0;
        while (i < _text.Length && _text[i] == '=')
        {
            level++;
            i++;
        }
        return i < _text.Length && _text[i] == '[' ? level : -1;
    }

    private void ReadLongBracket(int level, int start, int line, int col, string what)
    {
        _pos += level + 2;
        var close = "]" + new string('=', level) + "]";
        while (_pos < _text.Length)
        {
            if (_text[_pos] == ']' && string.CompareOrdinal(_text, _pos, close, 0, close.Length) == 0)
            {
                _pos += close.Length;
                return;
            }
            var c = _text[_pos++];
            if (c == '\n') NewLine();
            else if (c == '\r')
            {
                if (_pos < _text.Length && _text[_pos] == '\n') _pos++;
                NewLine();
            }
        }
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "LUA002",
            $"Nicht abgeschlossener langer {what} (erwartet '{close}').", start, Math.Min(level + 2, _text.Length - start), line, col));
    }

    private void ReadNumber(int start, int line, int col)
    {
        if (_text[_pos] == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
        {
            _pos += 2;
            while (_pos < _text.Length && (Uri.IsHexDigit(_text[_pos]) || _text[_pos] == '.')) _pos++;
            if (_pos < _text.Length && (_text[_pos] == 'p' || _text[_pos] == 'P'))
            {
                _pos++;
                if (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-')) _pos++;
                while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos])) _pos++;
            }
        }
        else
        {
            while (_pos < _text.Length && (char.IsAsciiDigit(_text[_pos]) || _text[_pos] == '.')) _pos++;
            if (_pos < _text.Length && (_text[_pos] == 'e' || _text[_pos] == 'E'))
            {
                _pos++;
                if (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-')) _pos++;
                while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos])) _pos++;
            }
        }
        // LuaJIT suffixes: ULL, LL, i
        while (_pos < _text.Length && (_text[_pos] == 'U' || _text[_pos] == 'L' || _text[_pos] == 'u' || _text[_pos] == 'l' || _text[_pos] == 'i')) _pos++;

        if (_pos < _text.Length && IsIdentPart(_text[_pos]))
        {
            while (_pos < _text.Length && IsIdentPart(_text[_pos])) _pos++;
            _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "LUA003", $"Ungültige Zahl '{_text.Substring(start, _pos - start)}'.", start, _pos - start, line, col));
            return;
        }
        var text = _text.Substring(start, _pos - start);
        if (text.Count(ch => ch == '.') > 1)
        {
            _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "LUA003", $"Ungültige Zahl '{text}'.", start, text.Length, line, col));
        }
    }

    private string ReadQuotedString(char quote, int start, int line, int col)
    {
        _pos++;
        var sb = new StringBuilder();
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c == quote)
            {
                _pos++;
                return sb.ToString();
            }
            if (c == '\n' || c == '\r')
            {
                break;
            }
            if (c == '\\')
            {
                _pos++;
                if (_pos >= _text.Length) break;
                var e = _text[_pos];
                switch (e)
                {
                    case 'n': sb.Append('\n'); _pos++; break;
                    case 't': sb.Append('\t'); _pos++; break;
                    case 'r': sb.Append('\r'); _pos++; break;
                    case 'a': sb.Append('\a'); _pos++; break;
                    case 'b': sb.Append('\b'); _pos++; break;
                    case 'f': sb.Append('\f'); _pos++; break;
                    case 'v': sb.Append('\v'); _pos++; break;
                    case '\\': sb.Append('\\'); _pos++; break;
                    case '"': sb.Append('"'); _pos++; break;
                    case '\'': sb.Append('\''); _pos++; break;
                    case '\n':
                        sb.Append('\n'); _pos++; NewLine(); break;
                    case '\r':
                        sb.Append('\n'); _pos++;
                        if (_pos < _text.Length && _text[_pos] == '\n') _pos++;
                        NewLine();
                        break;
                    case 'x':
                        _pos++;
                        var hex = 0;
                        var n = 0;
                        while (n < 2 && _pos < _text.Length && Uri.IsHexDigit(_text[_pos]))
                        {
                            hex = hex * 16 + Convert.ToInt32(_text[_pos].ToString(), 16);
                            _pos++;
                            n++;
                        }
                        if (n == 0) EscapeError(_pos - 2, "\\x benötigt hexadezimale Ziffern.");
                        sb.Append((char)hex);
                        break;
                    case 'z':
                        _pos++;
                        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                        {
                            if (_text[_pos] == '\n') { _pos++; NewLine(); }
                            else _pos++;
                        }
                        break;
                    case 'u':
                        _pos++;
                        if (_pos < _text.Length && _text[_pos] == '{')
                        {
                            var close = _text.IndexOf('}', _pos);
                            if (close > _pos)
                            {
                                var hexText = _text.Substring(_pos + 1, close - _pos - 1);
                                if (int.TryParse(hexText, System.Globalization.NumberStyles.HexNumber, null, out var cp) && cp <= 0x10FFFF)
                                {
                                    try { sb.Append(char.ConvertFromUtf32(cp)); } catch { /* surrogate range */ }
                                }
                                _pos = close + 1;
                                break;
                            }
                        }
                        EscapeError(_pos - 2, "Ungültige \\u{XXX} Escape-Sequenz.");
                        break;
                    default:
                        if (char.IsAsciiDigit(e))
                        {
                            var val = 0;
                            var digits = 0;
                            while (digits < 3 && _pos < _text.Length && char.IsAsciiDigit(_text[_pos]))
                            {
                                val = val * 10 + (_text[_pos] - '0');
                                _pos++;
                                digits++;
                            }
                            if (val > 255) EscapeError(_pos - digits - 1, "Dezimale Escape-Sequenz zu groß (max. \\255).");
                            sb.Append((char)Math.Min(val, 255));
                        }
                        else
                        {
                            EscapeError(_pos - 1, $"Ungültige Escape-Sequenz '\\{e}'.");
                            sb.Append(e);
                            _pos++;
                        }
                        break;
                }
                continue;
            }
            sb.Append(c);
            _pos++;
        }
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "LUA004", "Nicht abgeschlossener String.", start, _pos - start, line, col)
        {
            Fixes = { new CodeFix("Anführungszeichen ergänzen", new TextEdit(_pos, 0, quote.ToString())) },
        });
        return sb.ToString();
    }

    private void EscapeError(int offset, string message)
    {
        offset = Math.Clamp(offset, 0, _text.Length);
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "LUA005", message, offset, 2, _line, offset - _lineStart + 1));
    }
}
