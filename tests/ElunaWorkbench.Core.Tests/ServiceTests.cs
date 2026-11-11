using ElunaWorkbench.Core.Analysis;
using ElunaWorkbench.Core.Projects;
using ElunaWorkbench.Core.Services;

namespace ElunaWorkbench.Core.Tests;

public class ServiceTests
{
    private static (SemanticModel Model, int Caret) WithCaret(string code, string path = "server/test.lua")
    {
        var caret = code.IndexOf('|');
        code = code.Remove(caret, 1);
        return (Analyzer.Analyze(code, new AnalysisContext { FilePath = path }), caret);
    }

    [Fact]
    public void CompletesPlayerMethodsInHandler()
    {
        var (model, caret) = WithCaret("""
            local function OnLogin(event, player)
                player:Send|
            end
            RegisterPlayerEvent(3, OnLogin)
            """);
        var result = LanguageService.GetCompletions(model, caret);
        Assert.True(result.IsMemberCompletion);
        Assert.Contains(result.Items, i => i.Label == "SendBroadcastMessage");
        Assert.All(result.Items, i => Assert.Equal(CompletionKind.Method, i.Kind));
    }

    [Fact]
    public void CompletesChainedMethodReturnTypes()
    {
        var (model, caret) = WithCaret("""
            RegisterPlayerEvent(3, function(event, player)
                player:GetMap():|
            end)
            """);
        var result = LanguageService.GetCompletions(model, caret);
        Assert.Contains(result.Items, i => i.Label == "GetInstanceId");
    }

    [Fact]
    public void CompletesLibraryAndAio()
    {
        var (model, caret) = WithCaret("local s = string.|");
        Assert.Contains(LanguageService.GetCompletions(model, caret).Items, i => i.Label == "format");
        (model, caret) = WithCaret("local AIO = AIO or require(\"AIO\")\nAIO.|");
        var items = LanguageService.GetCompletions(model, caret).Items;
        Assert.Contains(items, i => i.Label == "AddHandlers");
        Assert.Contains(items, i => i.Label == "Handle");
    }

    [Fact]
    public void CompletesEventIds()
    {
        var (model, caret) = WithCaret("RegisterPlayerEvent(|");
        var items = LanguageService.GetCompletions(model, caret).Items;
        Assert.Contains(items, i => i.Kind == CompletionKind.Event && i.InsertText == "3" && i.Label.Contains("PLAYER_EVENT_ON_LOGIN"));
        Assert.Equal(CompletionKind.Event, items[0].Kind);
    }

    [Fact]
    public void CompletesLocalsAndGlobals()
    {
        var (model, caret) = WithCaret("local myCounter = 1\nlocal x = myC|");
        var items = LanguageService.GetCompletions(model, caret).Items;
        Assert.Equal("myCounter", items[0].Label);
        (model, caret) = WithCaret("local p = GetPlayerBy|");
        items = LanguageService.GetCompletions(model, caret).Items;
        Assert.Contains(items, i => i.Label == "GetPlayerByName");
    }

    [Fact]
    public void NoCompletionInComments()
    {
        var (model, caret) = WithCaret("-- player:|");
        Assert.Empty(LanguageService.GetCompletions(model, caret).Items);
    }

    [Fact]
    public void EventSnippetCreatesTypedHandler()
    {
        var (model, caret) = WithCaret("PLAYER_EVENT_ON_LOG|");
        var item = LanguageService.GetCompletions(model, caret).Items.First(i => i.Label == "PLAYER_EVENT_ON_LOGIN");
        Assert.Contains("local function OnLogin(event, player)", item.InsertText);
        Assert.Contains("RegisterPlayerEvent(3, OnLogin)", item.InsertText);
        var (model2, caret2) = WithCaret("CREATURE_EVENT_ON_DIE|");
        var item2 = LanguageService.GetCompletions(model2, caret2).Items.First(i => i.Label == "CREATURE_EVENT_ON_DIED");
        Assert.Contains("RegisterCreatureEvent(ENTRY, 4, OnDied)", item2.InsertText);
    }

    [Fact]
    public void SignatureHelpForMethodsAndGlobals()
    {
        var (model, caret) = WithCaret("""
            RegisterPlayerEvent(3, function(event, player)
                player:Teleport(0, |
            end)
            """);
        var sig = LanguageService.GetSignatureHelp(model, caret);
        Assert.NotNull(sig);
        Assert.Equal("Teleport", sig!.Function.Name);
        Assert.Equal(1, sig.ActiveParameter);

        (model, caret) = WithCaret("RegisterPlayerEvent(|)");
        sig = LanguageService.GetSignatureHelp(model, caret);
        Assert.NotNull(sig);
        Assert.Contains("PLAYER_EVENT_ON_LOGIN", sig!.Extra);
    }

    [Fact]
    public void HoverShowsMethodDocAndEvents()
    {
        var code = "RegisterPlayerEvent(3, function(event, player) player:GetName() end)";
        var model = Analyzer.Analyze(code);
        var hover = LanguageService.GetHover(model, code.IndexOf("GetName") + 2);
        Assert.NotNull(hover);
        Assert.Contains("GetName", hover!.Title);
        var evHover = LanguageService.GetHover(model, code.IndexOf('3'));
        Assert.Contains("PLAYER_EVENT_ON_LOGIN", evHover!.Title);
        var pHover = LanguageService.GetHover(model, code.IndexOf("player:") + 1);
        Assert.Contains("Player", pHover!.Title);
    }

    [Fact]
    public void DefinitionOfLocalFunction()
    {
        var code = "local function Foo() end\nFoo()";
        var model = Analyzer.Analyze(code);
        var def = LanguageService.GetDefinition(model, code.LastIndexOf("Foo") + 1);
        Assert.NotNull(def);
        Assert.Equal(code.IndexOf("Foo"), def!.Offset);
    }

    [Fact]
    public void FormatterReindents()
    {
        var code = "local function a()\nif x then\nprint(1)\nelse\nprint(2)\nend\nend";
        var formatted = Formatter.Format(code);
        Assert.Equal("local function a()\n    if x then\n        print(1)\n    else\n        print(2)\n    end\nend", formatted);
    }

    [Fact]
    public void SnippetExpand()
    {
        var (text, caret) = Snippets.Expand("if $0 then\n\tx\nend", "  ");
        Assert.Equal("if  then\n      x\n  end", text);
        Assert.Equal(3, caret);
    }

    [Fact]
    public void WorkspaceSharesGlobalsAcrossFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ewtest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = ElunaProject.Create(dir, "Test", ProjectTemplate.Empty);
            File.WriteAllText(Path.Combine(dir, "server", "a.lua"), "MyLib = {}\nfunction MyLib.Hello(player) end");
            File.WriteAllText(Path.Combine(dir, "server", "b.lua"), "MyLib.Hello(nil)\nMyLib.Helo(nil)");
            var ws = new Workspace(project);
            ws.LoadProjectFiles();
            var b = ws.GetModel(Path.Combine(dir, "server", "b.lua"))!;
            Assert.DoesNotContain(b.Diagnostics, d => d.Code == "ELU007");
            var (model, caret) = (ws.AnalyzeText(Path.Combine(dir, "server", "b.lua"), "MyLib."), 6);
            Assert.Contains(LanguageService.GetCompletions(model, caret).Items, i => i.Label == "Hello");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(ProjectTemplate.ElunaScripts)]
    [InlineData(ProjectTemplate.AioAddon)]
    public void ProjectTemplatesAreClean(ProjectTemplate template)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ewtest_" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = ElunaProject.Create(dir, "My Addon", template);
            var ws = new Workspace(project);
            ws.LoadProjectFiles();
            var problems = ws.AllDiagnostics().Where(d => d.Severity >= Lua.DiagnosticSeverity.Warning).ToList();
            Assert.True(problems.Count == 0, string.Join("\n", problems.Select(p => $"{Path.GetFileName(p.FilePath)}: {p}")));
            Assert.True(File.Exists(project.FilePath));
            var loaded = ElunaProject.Load(project.FilePath);
            Assert.Equal("My Addon", loaded.Name);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FileTemplatesAreClean()
    {
        foreach (var t in FileTemplates.All)
        {
            var path = t == FileTemplates.AioClient ? "client/x.lua" : "server/x.lua";
            var model = Analyzer.Analyze(t.Content, new AnalysisContext { FilePath = path, TargetCore = "TrinityCore" });
            var problems = model.Diagnostics.Where(d => d.Severity >= Lua.DiagnosticSeverity.Warning).ToList();
            Assert.True(problems.Count == 0, t.Name + ":\n" + string.Join("\n", problems));
        }
    }
}
