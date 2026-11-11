namespace ElunaWorkbench.Core.Projects;

public enum ProjectTemplate
{
    Empty,
    ElunaScripts,
    AioAddon,
}

public sealed record FileTemplate(string Name, string Description, string DefaultFileName, string Content);

public static class ProjectTemplates
{
    public static IReadOnlyList<(ProjectTemplate Template, string Name, string Description)> All { get; } = new[]
    {
        (ProjectTemplate.Empty, "Leeres Projekt", "Nur die Projektdatei und ein leerer Ordner 'server'."),
        (ProjectTemplate.ElunaScripts, "Eluna-Serverskripte", "Ordnerstruktur server/, lib/ mit Beispielen für Player-, Creature- und Gossip-Events."),
        (ProjectTemplate.AioAddon, "AIO-Addon (Server + Client)", "Ordnerstruktur server/, client/, shared/ mit einem lauffähigen AIO-Beispiel-Addon."),
    };

    public static void Apply(ProjectTemplate template, string root, string projectName)
    {
        var safe = ElunaProject.SanitizeFileName(projectName).Replace(' ', '_');
        switch (template)
        {
            case ProjectTemplate.Empty:
                Directory.CreateDirectory(Path.Combine(root, "server"));
                break;
            case ProjectTemplate.ElunaScripts:
                Write(root, "server/player_events.lua", FileTemplates.PlayerEvents.Content);
                Write(root, "server/creature_ai.lua", FileTemplates.CreatureAi.Content);
                Write(root, "server/gossip_npc.lua", FileTemplates.GossipNpc.Content);
                Write(root, "lib/utils.lua", UtilsLib);
                Write(root, "README.md", Readme(projectName, false));
                break;
            case ProjectTemplate.AioAddon:
                Write(root, $"server/{safe}_server.lua", AioServer(safe));
                Write(root, $"client/{safe}_client.lua", AioClient(safe));
                Write(root, "shared/config.lua", SharedConfig(safe));
                Write(root, "README.md", Readme(projectName, true));
                break;
        }
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, content.Replace("\r\n", "\n"));
    }

    private static string Readme(string name, bool aio) => $"""
        # {name}

        Erstellt mit Eluna Workbench.

        - `server/` – Eluna-Skripte, die auf dem Worldserver laufen (Ordner `lua_scripts`).
        {(aio ? "- `client/` – AIO-Client-Code, wird über AIO an den WoW-Client gesendet.\n- `shared/` – gemeinsam genutzte Konfiguration.\n\nVoraussetzung: AIO (https://github.com/Rochet2/AIO) – `AIO_Server` in `lua_scripts`, `AIO_Client` in `Interface/AddOns`." : "- `lib/` – Hilfsfunktionen.")}

        Mit *Projekt → Deploy* werden alle Lua-Dateien in den konfigurierten `lua_scripts`-Ordner kopiert.
        """;

    private const string UtilsLib = """
        -- Gemeinsame Hilfsfunktionen. Globale Tabellen sind in Eluna für alle Skripte sichtbar.
        Utils = Utils or {}

        --- Formatiert Kupfer als "Xg Ys Zc".
        function Utils.FormatMoney(copper)
            local gold = math.floor(copper / 10000)
            local silver = math.floor((copper % 10000) / 100)
            return string.format("%dg %ds %dc", gold, silver, copper % 100)
        end

        --- Sendet allen Spielern in der Welt eine Nachricht.
        function Utils.Broadcast(message)
            for _, player in ipairs(GetPlayersInWorld()) do
                player:SendBroadcastMessage(message)
            end
        end
        """;

    private static string AioServer(string name) => """
        local AIO = AIO or require("AIO")

        -- Sendet die Client-Datei an alle Spieler (nur auf dem Server, Haupt-State).
        AIO.AddAddon("lua_scripts/client/{{name}}_client.lua", "{{name}}")

        local Handlers = AIO.AddHandlers("{{name}}", {})

        --- Wird vom Client aufgerufen: AIO.Handle("{{name}}", "RequestGold")
        function Handlers.RequestGold(player)
            local money = player:GetCoinage()
            AIO.Handle(player, "{{name}}", "ShowGold", money)
        end

        local PLAYER_EVENT_ON_LOGIN = 3

        local function OnLogin(event, player)
            player:SendBroadcastMessage("{{name}} geladen. Tippe /{{name:L}} im Chat.")
        end

        RegisterPlayerEvent(PLAYER_EVENT_ON_LOGIN, OnLogin)
        """.Replace("{{name:L}}", name.ToLowerInvariant()).Replace("{{name}}", name);

    private static string AioClient(string name) => """
        local AIO = AIO or require("AIO")

        -- Auf dem Server wird die Datei nur registriert, auf dem Client ausgeführt.
        if AIO.AddAddon() then return end

        local Handlers = AIO.AddHandlers("{{name}}", {})

        local frame = CreateFrame("Frame", "{{name}}Frame", UIParent)
        frame:SetSize(220, 80)
        frame:SetPoint("CENTER")
        frame:Hide()

        local text = frame:CreateFontString(nil, "OVERLAY", "GameFontNormal")
        text:SetPoint("CENTER")

        --- Wird vom Server aufgerufen: AIO.Handle(player, "{{name}}", "ShowGold", money)
        function Handlers.ShowGold(player, money)
            text:SetText("Gold: " .. math.floor(money / 10000))
            frame:Show()
        end

        SLASH_{{NAME}}1 = "/{{name:L}}"
        SlashCmdList["{{NAME}}"] = function()
            AIO.Handle("{{name}}", "RequestGold")
        end
        """.Replace("{{NAME}}", name.ToUpperInvariant()).Replace("{{name:L}}", name.ToLowerInvariant()).Replace("{{name}}", name);

    private static string SharedConfig(string name) => """
        -- Gemeinsame Konfiguration für {{name}}.
        {{name}}Config = {
            Version = 1,
            Debug = false,
        }
        """.Replace("{{name}}", name);
}

public static class FileTemplates
{
    public static readonly FileTemplate Empty = new("Leere Lua-Datei", "Eine leere Datei.", "script.lua", "");

    public static readonly FileTemplate PlayerEvents = new("Player-Events", "Login/Logout/Chat-Events eines Spielers.", "player_events.lua", """
        local PLAYER_EVENT_ON_LOGIN  = 3
        local PLAYER_EVENT_ON_LOGOUT = 4
        local PLAYER_EVENT_ON_CHAT   = 18

        local function OnLogin(event, player)
            player:SendBroadcastMessage("Willkommen zurück, " .. player:GetName() .. "!")
        end

        local function OnLogout(event, player)
            print(player:GetName() .. " hat sich ausgeloggt.")
        end

        local function OnChat(event, player, msg, Type, lang)
            if msg == "#ping" then
                player:SendBroadcastMessage("pong")
                return false -- Nachricht nicht weiterleiten
            end
        end

        RegisterPlayerEvent(PLAYER_EVENT_ON_LOGIN, OnLogin)
        RegisterPlayerEvent(PLAYER_EVENT_ON_LOGOUT, OnLogout)
        RegisterPlayerEvent(PLAYER_EVENT_ON_CHAT, OnChat)
        """);

    public static readonly FileTemplate CreatureAi = new("Creature-AI (Boss)", "Kampf-Events einer Kreatur mit Timer.", "creature_ai.lua", """
        local NPC_ENTRY = 123456

        local CREATURE_EVENT_ON_ENTER_COMBAT = 1
        local CREATURE_EVENT_ON_LEAVE_COMBAT = 2
        local CREATURE_EVENT_ON_DIED         = 4

        local SPELL_FIREBALL = 133

        local function CastFireball(eventId, delay, repeats, creature)
            local target = creature:GetVictim()
            if target then
                creature:CastSpell(target, SPELL_FIREBALL, false)
            end
        end

        local function OnEnterCombat(event, creature, target)
            creature:SendUnitYell("Ihr werdet brennen!", 0)
            creature:RegisterEvent(CastFireball, 5000, 0)
        end

        local function OnLeaveCombat(event, creature)
            creature:RemoveEvents()
        end

        local function OnDied(event, creature, killer)
            creature:RemoveEvents()
            creature:SendUnitSay("Unmöglich...", 0)
        end

        RegisterCreatureEvent(NPC_ENTRY, CREATURE_EVENT_ON_ENTER_COMBAT, OnEnterCombat)
        RegisterCreatureEvent(NPC_ENTRY, CREATURE_EVENT_ON_LEAVE_COMBAT, OnLeaveCombat)
        RegisterCreatureEvent(NPC_ENTRY, CREATURE_EVENT_ON_DIED, OnDied)
        """);

    public static readonly FileTemplate GossipNpc = new("Gossip-NPC", "NPC mit Gossip-Menü (Teleporter).", "gossip_npc.lua", """
        local NPC_ENTRY = 123457

        local GOSSIP_EVENT_ON_HELLO  = 1
        local GOSSIP_EVENT_ON_SELECT = 2

        local function OnGossipHello(event, player, object)
            player:GossipClearMenu()
            player:GossipMenuAddItem(0, "Teleportiere mich nach Sturmwind", 0, 1)
            player:GossipMenuAddItem(0, "Auf Wiedersehen", 0, 2)
            player:GossipSendMenu(1, object)
        end

        local function OnGossipSelect(event, player, object, sender, intid, code, menu_id)
            if intid == 1 then
                player:Teleport(0, -8833.38, 628.62, 94.00, 1.06)
            end
            player:GossipComplete()
        end

        RegisterCreatureGossipEvent(NPC_ENTRY, GOSSIP_EVENT_ON_HELLO, OnGossipHello)
        RegisterCreatureGossipEvent(NPC_ENTRY, GOSSIP_EVENT_ON_SELECT, OnGossipSelect)
        """);

    public static readonly FileTemplate ServerEvents = new("Server-Events / Timer", "Weltweiter Timer und Server-Start-Event.", "server_events.lua", """
        local WORLD_EVENT_ON_STARTUP = 14

        local function Announce(eventId, delay, repeats)
            SendWorldMessage("Dieser Server läuft mit Eluna!")
        end

        local function OnStartup(event)
            print("[Eluna] Server gestartet.")
            CreateLuaEvent(Announce, 15 * 60 * 1000, 0) -- alle 15 Minuten
        end

        RegisterServerEvent(WORLD_EVENT_ON_STARTUP, OnStartup)
        """);

    public static readonly FileTemplate DatabaseQuery = new("Datenbank-Abfrage", "Beispiel für WorldDBQuery mit Ergebnisschleife.", "db_query.lua", """
        local function PrintCreatureNames(entryFrom, entryTo)
            local query = WorldDBQuery(string.format(
                "SELECT entry, name FROM creature_template WHERE entry BETWEEN %d AND %d", entryFrom, entryTo))
            if not query then
                print("Keine Ergebnisse.")
                return
            end
            repeat
                local entry = query:GetUInt32(0)
                local name = query:GetString(1)
                print(entry, name)
            until not query:NextRow()
        end

        PrintCreatureNames(1, 10)
        """);

    public static readonly FileTemplate AioClient = new("AIO-Client-Addon", "Client-Datei mit AIO.AddAddon()-Muster.", "addon_client.lua", """
        local AIO = AIO or require("AIO")
        if AIO.AddAddon() then return end

        local Handlers = AIO.AddHandlers("MyAddon", {})

        function Handlers.Hello(player, text)
            print("Server sagt: " .. tostring(text))
        end
        """);

    public static readonly FileTemplate AioServer = new("AIO-Server-Handler", "Server-Datei mit AIO-Handlern.", "addon_server.lua", """
        local AIO = AIO or require("AIO")

        local Handlers = AIO.AddHandlers("MyAddon", {})

        function Handlers.Ping(player)
            AIO.Handle(player, "MyAddon", "Hello", "pong")
        end
        """);

    public static IReadOnlyList<FileTemplate> All { get; } = new[]
    {
        Empty, PlayerEvents, CreatureAi, GossipNpc, ServerEvents, DatabaseQuery, AioServer, AioClient,
    };
}
