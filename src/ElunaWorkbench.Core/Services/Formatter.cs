using System.Text;
using ElunaWorkbench.Core.Lua;

namespace ElunaWorkbench.Core.Services;

/// <summary>Re-indents Lua code based on its block structure.</summary>
public static class Formatter
{
    public static string Format(string text, string indentUnit = "    ")
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = new LineIndex(text);
        var tokens = Lexer.Tokenize(text);
        var levels = ComputeLineLevels(text, tokens, lines, out var untouchable);

        var sb = new StringBuilder();
        for (var l = 1; l <= lines.LineCount; l++)
        {
            var start = lines.GetLineStart(l);
            var end = l < lines.LineCount ? lines.GetLineStart(l + 1) : text.Length;
            var raw = text.Substring(start, end - start).TrimEnd('\r', '\n');
            if (untouchable.Contains(l))
            {
                sb.Append(raw);
            }
            else
            {
                var content = raw.Trim();
                if (content.Length > 0)
                {
                    for (var i = 0; i < Math.Max(0, levels[l]); i++) sb.Append(indentUnit);
                    sb.Append(content);
                }
            }
            if (l < lines.LineCount) sb.Append(newline);
        }
        return sb.ToString();
    }

    /// <summary>Indentation level for a new line inserted after the given line (auto-indent).</summary>
    public static int IndentLevelAfter(string textUpToCaret)
    {
        var tokens = Lexer.Tokenize(textUpToCaret);
        var lines = new LineIndex(textUpToCaret);
        var levels = ComputeLineLevels(textUpToCaret, tokens, lines, out _, out var finalLevel);
        _ = levels;
        return Math.Max(0, finalLevel);
    }

    private static int[] ComputeLineLevels(string text, List<Token> tokens, LineIndex lines, out HashSet<int> untouchable) =>
        ComputeLineLevels(text, tokens, lines, out untouchable, out _);

    private static int[] ComputeLineLevels(string text, List<Token> tokens, LineIndex lines, out HashSet<int> untouchable, out int finalLevel)
    {
        var levels = new int[lines.LineCount + 2];
        untouchable = new HashSet<int>();
        var byLine = new Dictionary<int, List<Token>>();
        foreach (var t in tokens)
        {
            if (t.Kind == TokenKind.EndOfFile) continue;
            var (startLine, _) = lines.GetPosition(t.Start);
            var (endLine, _) = lines.GetPosition(t.End);
            if ((t.Kind == TokenKind.String || t.Kind == TokenKind.Comment) && endLine > startLine)
            {
                for (var l = startLine + 1; l <= endLine; l++) untouchable.Add(l);
            }
            if (t.Kind == TokenKind.Comment) continue;
            if (!byLine.TryGetValue(startLine, out var list)) byLine[startLine] = list = new List<Token>();
            list.Add(t);
        }

        var level = 0;
        for (var l = 1; l <= lines.LineCount; l++)
        {
            var lineTokens = byLine.TryGetValue(l, out var lt) ? lt : new List<Token>();
            // Leading closers dedent the line itself.
            var leadingClose = 0;
            foreach (var t in lineTokens)
            {
                if (IsCloser(t) || t.IsKeyword("else") || t.IsKeyword("elseif")) leadingClose++;
                else break;
                if (t.IsKeyword("else") || t.IsKeyword("elseif")) break;
            }
            levels[l] = level - leadingClose;
            foreach (var t in lineTokens)
            {
                if (IsOpener(t)) level++;
                else if (IsCloser(t) && !t.IsKeyword("else")) level--;
            }
            if (level < 0) level = 0;
        }
        finalLevel = level;
        return levels;
    }

    private static bool IsOpener(Token t) =>
        t.Kind == TokenKind.Keyword && t.Text is "function" or "do" or "then" or "repeat" ||
        t.Kind == TokenKind.Operator && t.Text is "{" or "(";

    private static bool IsCloser(Token t) =>
        t.Kind == TokenKind.Keyword && t.Text is "end" or "until" or "elseif" or "else" ||
        t.Kind == TokenKind.Operator && t.Text is "}" or ")";
}
