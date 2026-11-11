using System.Security;
using System.Text;
using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using ElunaWorkbench.Core.Api;

namespace ElunaWorkbench.Editor;

/// <summary>
/// Builds the syntax highlighting definition for Lua with Eluna/AIO aware keyword lists
/// (global Eluna functions, Lua standard library, AIO, method calls after ':').
/// </summary>
public static class LuaHighlighting
{
    private static IHighlightingDefinition? _definition;

    public static IHighlightingDefinition Definition => _definition ??= Build(ApiDatabase.Default);

    private static IHighlightingDefinition Build(ApiDatabase api)
    {
        var elunaGlobals = api.Globals.Where(g => g.Source == ApiSource.Eluna).Select(g => g.Name);
        var luaGlobals = api.Globals.Where(g => g.Source == ApiSource.LuaStdLib).Select(g => g.Name)
            .Concat(api.Libraries.Where(l => l.Source == ApiSource.LuaStdLib).Select(l => l.Name))
            .Concat(new[] { "_G", "_VERSION", "_ENV", "self" });
        var wowGlobals = api.Globals.Where(g => g.Source == ApiSource.WowClient).Select(g => g.Name)
            .Concat(api.GlobalFields.Where(f => f.Source == ApiSource.WowClient).Select(f => f.Name));

        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0"?>
            <SyntaxDefinition name="Lua (Eluna)" extensions=".lua" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="#6A9955" fontStyle="italic" />
              <Color name="DocComment" foreground="#7CA668" fontStyle="italic" />
              <Color name="String" foreground="#CE9178" />
              <Color name="Escape" foreground="#D7BA7D" />
              <Color name="Number" foreground="#B5CEA8" />
              <Color name="Keyword" foreground="#569CD6" fontWeight="bold" />
              <Color name="Control" foreground="#C586C0" fontWeight="bold" />
              <Color name="Constant" foreground="#569CD6" />
              <Color name="ElunaGlobal" foreground="#DCDCAA" fontWeight="bold" />
              <Color name="LuaGlobal" foreground="#4EC9B0" />
              <Color name="Aio" foreground="#4EC9B0" fontWeight="bold" />
              <Color name="WowGlobal" foreground="#9CDCFE" />
              <Color name="Method" foreground="#DCDCAA" />
              <Color name="UpperConstant" foreground="#4FC1FF" />
              <Color name="Label" foreground="#C586C0" />

              <RuleSet>
                <Span color="Comment" multiline="true"><Begin>--\[\[</Begin><End>\]\]</End></Span>
                <Span color="Comment" multiline="true"><Begin>--\[=\[</Begin><End>\]=\]</End></Span>
                <Span color="Comment" multiline="true"><Begin>--\[==\[</Begin><End>\]==\]</End></Span>
                <Span color="DocComment"><Begin>---</Begin></Span>
                <Span color="Comment"><Begin>--</Begin></Span>
                <Span color="String" multiline="true"><Begin>\[\[</Begin><End>\]\]</End></Span>
                <Span color="String" multiline="true"><Begin>\[=\[</Begin><End>\]=\]</End></Span>
                <Span color="String" multiline="true"><Begin>\[==\[</Begin><End>\]==\]</End></Span>
                <Span color="String">
                  <Begin>"</Begin><End>"</End>
                  <RuleSet><Span color="Escape" begin="\\" end="." /></RuleSet>
                </Span>
                <Span color="String">
                  <Begin>'</Begin><End>'</End>
                  <RuleSet><Span color="Escape" begin="\\" end="." /></RuleSet>
                </Span>
                <Keywords color="Keyword">
                  <Word>local</Word><Word>function</Word><Word>end</Word><Word>and</Word><Word>or</Word><Word>not</Word><Word>in</Word>
                </Keywords>
                <Keywords color="Control">
                  <Word>if</Word><Word>then</Word><Word>else</Word><Word>elseif</Word><Word>for</Word><Word>while</Word><Word>do</Word>
                  <Word>repeat</Word><Word>until</Word><Word>return</Word><Word>break</Word><Word>goto</Word>
                </Keywords>
                <Keywords color="Constant">
                  <Word>nil</Word><Word>true</Word><Word>false</Word>
                </Keywords>
                <Keywords color="Aio">
                  <Word>AIO</Word>
                </Keywords>

            """);
        AppendKeywords(sb, "ElunaGlobal", elunaGlobals);
        AppendKeywords(sb, "LuaGlobal", luaGlobals);
        AppendKeywords(sb, "WowGlobal", wowGlobals);
        sb.Append("""
                <Rule color="Label">::[A-Za-z_]\w*::</Rule>
                <Rule color="Method">(?&lt;=:)[A-Za-z_]\w*(?=\s*[\(\{"'])</Rule>
                <Rule color="UpperConstant">\b[A-Z][A-Z0-9]*_[A-Z0-9_]+\b</Rule>
                <Rule color="Number">\b0[xX][0-9a-fA-F]+(\.[0-9a-fA-F]*)?([pP][+-]?\d+)?|(\b\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """);

        using var reader = XmlReader.Create(new StringReader(sb.ToString()));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static void AppendKeywords(StringBuilder sb, string color, IEnumerable<string> words)
    {
        var list = words.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct().ToList();
        if (list.Count == 0) return;
        sb.Append("    <Keywords color=\"").Append(color).Append("\">");
        foreach (var w in list) sb.Append("<Word>").Append(SecurityElement.Escape(w)).Append("</Word>");
        sb.Append("</Keywords>\n");
    }
}
