using System.Text;
using ElunaWorkbench.Core.Analysis;
using ElunaWorkbench.Core.Api;

namespace ElunaWorkbench.Core.Services;

/// <summary>
/// Code snippets. "$0" marks the caret position after insertion; tabs are converted to the
/// editor's indentation. Every Eluna event is available as a snippet (e.g. PLAYER_EVENT_ON_LOGIN)
/// that creates a handler with the correct parameters and registers it.
/// </summary>
public static class Snippets
{
    private static readonly (string Label, string Detail, string Body)[] Static =
    {
        ("lfunc", "local function", "local function ${name}(${params})\n\t$0\nend"),
        ("func", "function", "function ${name}(${params})\n\t$0\nend"),
        ("if", "if … then … end", "if $0 then\n\t\nend"),
        ("ifelse", "if … else … end", "if $0 then\n\t\nelse\n\t\nend"),
        ("fori", "numerische for-Schleife", "for i = 1, $0 do\n\t\nend"),
        ("forp", "for k, v in pairs()", "for k, v in pairs($0) do\n\t\nend"),
        ("foripairs", "for i, v in ipairs()", "for i, v in ipairs($0) do\n\t\nend"),
        ("while", "while-Schleife", "while $0 do\n\t\nend"),
        ("repeat", "repeat … until", "repeat\n\t$0\nuntil "),
        ("forplayers", "über alle Spieler iterieren", "for _, player in ipairs(GetPlayersInWorld()) do\n\t$0\nend"),
        ("dbquery", "WorldDBQuery mit Ergebnisschleife", "local query = WorldDBQuery(\"$0\")\nif query then\n\trepeat\n\t\tlocal value = query:GetUInt32(0)\n\tuntil not query:NextRow()\nend"),
        ("timer", "CreateLuaEvent (globaler Timer)", "CreateLuaEvent(function(eventId, delay, repeats)\n\t$0\nend, 1000, 1)"),
        ("objtimer", "obj:RegisterEvent (Objekt-Timer)", "creature:RegisterEvent(function(eventId, delay, repeats, creature)\n\t$0\nend, 1000, 1)"),
        ("gossip", "Gossip Hello + Select für einen NPC", "local NPC_ENTRY = $0\n\nlocal function OnGossipHello(event, player, object)\n\tplayer:GossipClearMenu()\n\tplayer:GossipMenuAddItem(0, \"Option 1\", 0, 1)\n\tplayer:GossipSendMenu(1, object)\nend\n\nlocal function OnGossipSelect(event, player, object, sender, intid, code, menu_id)\n\tif intid == 1 then\n\t\t\n\tend\n\tplayer:GossipComplete()\nend\n\nRegisterCreatureGossipEvent(NPC_ENTRY, 1, OnGossipHello)\nRegisterCreatureGossipEvent(NPC_ENTRY, 2, OnGossipSelect)"),
        ("aiorequire", "AIO einbinden", "local AIO = AIO or require(\"AIO\")\n$0"),
        ("aioclient", "AIO-Client-Datei (AddAddon-Muster)", "local AIO = AIO or require(\"AIO\")\nif AIO.AddAddon() then return end\n\nlocal Handlers = AIO.AddHandlers(\"${name}\", {})\n\nfunction Handlers.$0(player, ...)\n\t\nend"),
        ("aioserver", "AIO-Server-Handler", "local AIO = AIO or require(\"AIO\")\n\nlocal Handlers = AIO.AddHandlers(\"${name}\", {})\n\nfunction Handlers.$0(player, ...)\n\t\nend"),
        ("aiohandler", "AIO-Handlerfunktion", "function Handlers.$0(player, ...)\n\t\nend"),
        ("aiomsg", "AIO-Nachricht senden", "AIO.Msg():Add(\"${name}\", $0):Send(player)"),
    };

    public static IEnumerable<CompletionItem> All(SemanticModel model)
    {
        foreach (var (label, detail, body) in Static)
        {
            yield return new CompletionItem
            {
                Label = label,
                Kind = CompletionKind.Snippet,
                Detail = detail,
                Documentation = Preview(body),
                InsertText = Clean(body),
                Priority = 35,
            };
        }

        foreach (var hook in model.Api.Hooks)
        {
            foreach (var ev in hook.Events)
            {
                var body = EventSnippet(model.Api, hook, ev);
                yield return new CompletionItem
                {
                    Label = ev.Name,
                    Kind = CompletionKind.Event,
                    Detail = $"{hook.Register} – {ev.Id}",
                    Documentation = $"Erzeugt einen Handler für {ev.Name} und registriert ihn.\nParameter: {string.Join(", ", ev.Params.Select(p => $"{p.Name}: {p.Type}"))}" +
                                    (string.IsNullOrWhiteSpace(ev.Comment) ? "" : "\n" + ev.Comment) + "\n\n" + Preview(body),
                    InsertText = body,
                    Priority = 45,
                };
            }
        }
    }

    public static string EventSnippet(ApiDatabase api, ApiHook hook, ApiEvent ev)
    {
        var fnName = "On" + ToPascal(ev.Name);
        var ps = ev.Params.Count > 0 && ev.Params[0].Name != "..." ? ev.ParameterList : "event, ...";
        var fn = api.FindGlobal(hook.Register);
        var args = new List<string>();
        if (fn != null)
        {
            for (var i = 0; i < fn.Params.Count; i++)
            {
                if (i == hook.EventArgIndex) args.Add(ev.Id.ToString());
                else if (i == hook.HandlerArgIndex) args.Add(fnName);
                else if (fn.Params[i].IsOptional) break;
                else args.Add(fn.Params[i].Name.ToUpperInvariant());
            }
        }
        else
        {
            args.Add(ev.Id.ToString());
            args.Add(fnName);
        }
        var sb = new StringBuilder();
        sb.Append("local function ").Append(fnName).Append('(').Append(ps).Append(")\n\t$0\nend\n\n");
        sb.Append(hook.Register).Append('(').Append(string.Join(", ", args)).Append(")  -- ").Append(ev.Name);
        return sb.ToString();
    }

    private static string ToPascal(string eventName)
    {
        // PLAYER_EVENT_ON_LOGIN -> Login, CREATURE_EVENT_ON_DIED -> Died
        var idx = eventName.IndexOf("_ON_", StringComparison.Ordinal);
        var part = idx >= 0 ? eventName[(idx + 4)..] : eventName;
        return string.Concat(part.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    private static string Clean(string body) => body.Replace("${name}", "Name").Replace("${params}", "");

    private static string Preview(string body) => Clean(body).Replace("$0", "").Replace("\t", "    ");

    /// <summary>
    /// Expands a snippet for insertion: indents continuation lines with the current line
    /// indentation, converts tabs and returns the text plus the caret offset within it.
    /// </summary>
    public static (string Text, int Caret) Expand(string snippet, string lineIndent, string indentUnit = "    ")
    {
        var lines = snippet.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n').Append(lineIndent);
            var line = lines[i];
            var tabs = 0;
            while (tabs < line.Length && line[tabs] == '\t') tabs++;
            for (var t = 0; t < tabs; t++) sb.Append(indentUnit);
            sb.Append(line, tabs, line.Length - tabs);
        }
        var text = sb.ToString();
        var caret = text.IndexOf("$0", StringComparison.Ordinal);
        if (caret >= 0) text = text.Remove(caret, 2);
        else caret = text.Length;
        return (text, caret);
    }
}
