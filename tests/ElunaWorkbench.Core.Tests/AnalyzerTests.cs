using ElunaWorkbench.Core.Analysis;
using ElunaWorkbench.Core.Lua;

namespace ElunaWorkbench.Core.Tests;

public class AnalyzerTests
{
    private static SemanticModel Analyze(string code, string path = "server/test.lua", LuaVersion version = LuaVersion.Lua52, string? core = null) =>
        Analyzer.Analyze(code, new AnalysisContext { FilePath = path, LuaVersion = version, TargetCore = core });

    private static List<Diagnostic> Diags(string code, string? codeFilter = null, string path = "server/test.lua") =>
        Analyze(code, path).Diagnostics.Where(d => codeFilter == null || d.Code == codeFilter).ToList();

    [Fact]
    public void ValidScriptHasNoErrorsOrWarnings()
    {
        var code = """
            local PLAYER_EVENT_ON_LOGIN = 3

            local function OnLogin(event, player)
                player:SendBroadcastMessage("Willkommen, " .. player:GetName() .. "!")
                local guild = player:GetGuild()
                if guild then
                    print(guild:GetName())
                end
            end

            RegisterPlayerEvent(PLAYER_EVENT_ON_LOGIN, OnLogin)
            """;
        var diags = Diags(code).Where(d => d.Severity >= DiagnosticSeverity.Warning).ToList();
        Assert.Empty(diags);
    }

    [Fact]
    public void HandlerParametersGetTypesFromEvent()
    {
        var code = """
            RegisterPlayerEvent(7, function(event, killer, killed)
                killer:SendBroadcastMessage("kill")
            end)
            """;
        var model = Analyze(code);
        var killed = model.AllSymbols.Single(s => s.Name == "killed");
        Assert.Equal("Creature", killed.Type);
        var killer = model.AllSymbols.Single(s => s.Name == "killer");
        Assert.Equal("Player", killer.Type);
    }

    [Fact]
    public void UnknownMethodIsReportedWithSuggestion()
    {
        var code = """
            local function OnLogin(event, player)
                player:GetNmae()
            end
            RegisterPlayerEvent(3, OnLogin)
            """;
        var d = Assert.Single(Diags(code, "ELU004"));
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Contains(d.Fixes, f => f.Title.Contains("GetName"));
        var fixedCode = d.Fixes.First(f => f.Title.Contains("'GetName'")).Apply(code);
        Assert.Contains("player:GetName()", fixedCode);
    }

    [Fact]
    public void DotInsteadOfColonIsFixed()
    {
        var code = """
            RegisterPlayerEvent(3, function(event, player)
                local n = player.GetName()
                print(n)
            end)
            """;
        var d = Assert.Single(Diags(code, "ELU002"));
        Assert.Contains("player:GetName()", d.Fixes[0].Apply(code));
    }

    [Fact]
    public void InvalidEventIdIsReported()
    {
        var d = Assert.Single(Diags("RegisterPlayerEvent(999, function() end)", "ELU001"));
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.NotEmpty(d.Fixes);
    }

    [Fact]
    public void ReturnTypesFlowThroughLocals()
    {
        var code = """
            local player = GetPlayerByName("Test")
            local target = player:GetSelection()
            local map = target:GetMap()
            """;
        var model = Analyze(code);
        Assert.Equal("Player", model.AllSymbols.Single(s => s.Name == "player").Type);
        Assert.Equal("Unit", model.AllSymbols.Single(s => s.Name == "target").Type);
        Assert.Equal("Map", model.AllSymbols.Single(s => s.Name == "map").Type);
    }

    [Fact]
    public void ForeignOperatorsHaveFixes()
    {
        var code = "local a = 1\nif a != 2 && a > 0 then a += 1 end";
        var diags = Diags(code).Where(d => d.Code == "LUA111").ToList();
        Assert.Equal(3, diags.Count);
        var fixedCode = code;
        foreach (var d in diags.OrderByDescending(d => d.Start)) fixedCode = d.Fixes[0].Apply(fixedCode);
        Assert.Equal("local a = 1\nif a ~= 2 and a > 0 then a = a + 1 end", fixedCode);
        Assert.Empty(Diags(fixedCode).Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void MissingEndIsDetectedAndFixable()
    {
        var code = """
            local function A()
                if true then
                    print("x")
            end

            local function B()
                print("y")
            end
            """;
        var d = Diags(code).First(x => x.Code == "LUA102");
        Assert.NotEmpty(d.Fixes);
        var fixedCode = d.Fixes[0].Apply(code);
        Assert.DoesNotContain(Diags(fixedCode), x => x.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void UndefinedGlobalSuggestsApiFunction()
    {
        var d = Assert.Single(Diags("local p = GetPlayerByNam('x')\nprint(p)", "ELU007"));
        Assert.Contains(d.Fixes, f => f.Title.Contains("GetPlayerByName"));
    }

    [Fact]
    public void NullIsReplacedByNil()
    {
        var d = Assert.Single(Diags("local x = null\nprint(x)", "ELU006"));
        Assert.Equal("local x = nil\nprint(x)", d.Fixes[0].Apply("local x = null\nprint(x)"));
    }

    [Fact]
    public void UnusedLocalIsHint()
    {
        var d = Assert.Single(Diags("local unused = 5", "ELU009"));
        Assert.Equal(DiagnosticSeverity.Hint, d.Severity);
    }

    [Fact]
    public void AioHandlersAreTypedAndChecked()
    {
        var server = """
            local AIO = AIO or require("AIO")
            local MyHandlers = AIO.AddHandlers("MyAddon", {})

            function MyHandlers.Buy(plr, itemId)
                plr:AddItem(itemId, 1)
            end
            """;
        var client = """
            local AIO = AIO or require("AIO")
            if AIO.AddAddon() then return end
            AIO.Handle("MyAddon", "Buyy", 5)
            """;
        var ws = new Projects.Workspace();
        ws.SetText("/tmp/p/server/shop.lua", server, analyze: false);
        ws.SetText("/tmp/p/client/shop.lua", client, analyze: false);
        ws.AnalyzeAll();
        ws.AnalyzeAll();
        var serverModel = ws.GetModel("/tmp/p/server/shop.lua")!;
        Assert.Equal("Player", serverModel.AllSymbols.First(s => s.Name == "plr").Type);
        Assert.DoesNotContain(serverModel.Diagnostics, x => x.Severity >= DiagnosticSeverity.Warning);
        var clientModel = ws.GetModel("/tmp/p/client/shop.lua")!;
        var d = Assert.Single(clientModel.Diagnostics, x => x.Code == "AIO003");
        Assert.Contains(d.Fixes, f => f.Title.Contains("Buy"));
    }

    [Fact]
    public void AioMessageChainIsTyped()
    {
        var code = """
            local AIO = AIO or require("AIO")
            local msg = AIO.Msg():Add("Name", 1)
            msg:Sendd()
            """;
        var d = Assert.Single(Diags(code, "ELU004"));
        Assert.Contains("AIOMsg", d.Message);
    }

    [Fact]
    public void ClientFileIsDetectedByAddAddonPattern()
    {
        var code = """
            local AIO = AIO or require("AIO")
            if AIO.AddAddon() then return end
            local frame = CreateFrame("Frame", "MyFrame", UIParent)
            frame:SetSize(100, 100)
            """;
        var model = Analyze(code, "addon.lua");
        Assert.True(model.IsClientFile);
        Assert.DoesNotContain(model.Diagnostics, x => x.Severity >= DiagnosticSeverity.Warning);
    }

    [Fact]
    public void LuaVersionFeaturesAreChecked()
    {
        var model = Analyze("local a = 7 // 2\nprint(a)", version: LuaVersion.Lua52);
        Assert.Contains(model.Diagnostics, d => d.Code == "LUA120");
        model = Analyze("local a = 7 // 2\nprint(a)", version: LuaVersion.Lua53);
        Assert.DoesNotContain(model.Diagnostics, d => d.Code == "LUA120");
    }

    [Fact]
    public void ArgumentCountIsChecked()
    {
        Assert.Single(Diags("RegisterPlayerEvent(3)", "ELU010"));
        Assert.Empty(Diags("RegisterPlayerEvent(3, function() end)", "ELU010"));
    }

    [Fact]
    public void CoreCompatibilityIsChecked()
    {
        // AddLootMode exists only on TrinityCore / AzerothCore
        var code = "RegisterCreatureEvent(1, 5, function(event, creature) creature:AddLootMode(1) end)";
        Assert.Contains(Analyze(code, core: "Mangos").Diagnostics, d => d.Code == "ELU012");
        Assert.DoesNotContain(Analyze(code, core: "TrinityCore").Diagnostics, d => d.Code == "ELU012");
    }

    [Fact]
    public void ElseIfHint()
    {
        var code = "local a = 1\nif a then\nelse if a then end\nend";
        var d = Assert.Single(Diags(code, "LUA110"));
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
    }

    [Fact]
    public void ParserHandlesAllSyntax()
    {
        var code = """
            local t = { 1, 2, x = 3, ["y"] = 4; f = function(...) return ... end }
            local s = [[long
            string]] .. [==[ another ]==] .. "esc\n\t\"" .. 'single'
            --[[ block
            comment ]]
            for i = 1, 10, 2 do print(i) end
            for k, v in pairs(t) do print(k, v) end
            while false do break end
            repeat local z = 1 until z == 1
            local obj = {}
            function obj.a.b() end
            function obj:method() return self end
            local x = #t + -1 * 2 ^ 3 .. "" == "a" and not false or nil
            goto done
            ::done::
            print(s, x, 0x1F, 1e10, .5)
            """;
        var model = Analyze(code);
        Assert.DoesNotContain(model.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }
}
