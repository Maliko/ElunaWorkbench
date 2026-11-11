using System.Collections.ObjectModel;
using System.Text.Json;
using ElunaWorkbench.Core.Api;
using ElunaWorkbench.Core.Lua;

namespace ElunaWorkbench;

/// <summary>Node of the project explorer tree.</summary>
public sealed class ProjectNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsProjectFile { get; init; }
    public ObservableCollection<ProjectNode> Children { get; } = new();

    public string Icon => IsProjectFile ? "⚙" : IsDirectory ? "📁" : Name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ? "🌙" : "📄";

    public static ProjectNode Build(string directory, Func<string, bool> excluded, string? projectFile)
    {
        var root = new ProjectNode { Name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)), FullPath = directory, IsDirectory = true };
        Fill(root, excluded, projectFile);
        return root;
    }

    private static void Fill(ProjectNode node, Func<string, bool> excluded, string? projectFile)
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(node.FullPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (excluded(d)) continue;
                var child = new ProjectNode { Name = Path.GetFileName(d), FullPath = d, IsDirectory = true };
                Fill(child, excluded, projectFile);
                node.Children.Add(child);
            }
            foreach (var f in Directory.EnumerateFiles(node.FullPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".lua" or ".md" or ".txt" or ".ewproj" or ".conf" or ".toc" or ".json" or ".sql")) continue;
                node.Children.Add(new ProjectNode
                {
                    Name = Path.GetFileName(f),
                    FullPath = f,
                    IsProjectFile = projectFile != null && string.Equals(Path.GetFullPath(f), Path.GetFullPath(projectFile), StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public override string ToString() => Name;
}

/// <summary>Node of the API browser.</summary>
public sealed class ApiNode
{
    public required string Label { get; init; }
    public string Documentation { get; init; } = "";

    /// <summary>Text inserted on double click.</summary>
    public string? InsertText { get; init; }

    public ObservableCollection<ApiNode> Children { get; } = new();

    public override string ToString() => Label;

    public static List<ApiNode> BuildTree(ApiDatabase api, string filter)
    {
        var f = filter.Trim();
        bool Match(string s) => f.Length == 0 || s.Contains(f, StringComparison.OrdinalIgnoreCase);
        var roots = new List<ApiNode>();

        var global = new ApiNode { Label = "Eluna – Globale Funktionen", Documentation = "Globale Funktionen der Eluna Lua Engine." };
        foreach (var g in api.Globals.Where(g => g.Source == ApiSource.Eluna).OrderBy(g => g.Name))
        {
            if (!Match(g.Name)) continue;
            global.Children.Add(FromFunction(g, g.Name + "()"));
        }
        if (global.Children.Count > 0) roots.Add(global);

        var classes = new ApiNode { Label = "Eluna – Klassen", Documentation = "Objekttypen der Eluna API. Methoden werden mit ':' aufgerufen." };
        foreach (var c in api.Classes.Where(c => c.Source == ApiSource.Eluna).OrderBy(c => c.Name))
        {
            var node = new ApiNode
            {
                Label = c.Name + (c.Inherits.Count > 0 ? "  : " + string.Join(", ", c.Inherits) : ""),
                Documentation = $"Klasse {c.Name}\n{(c.Inherits.Count > 0 ? "Erbt von: " + string.Join(", ", c.Inherits) + "\n" : "")}\n{ApiText.StripLinks(c.Description)}\n\n{c.Methods.Count} eigene Methoden, {api.GetMethods(c.Name).Count} inkl. geerbter.",
            };
            var classMatch = Match(c.Name);
            foreach (var m in c.Methods.OrderBy(m => m.Name))
            {
                if (classMatch || Match(m.Name)) node.Children.Add(FromFunction(m, ":" + m.Name + "()"));
            }
            if (node.Children.Count > 0 || classMatch) classes.Children.Add(node);
        }
        if (classes.Children.Count > 0) roots.Add(classes);

        var events = new ApiNode { Label = "Eluna – Events", Documentation = "Event-IDs für die Register*Event-Funktionen. Doppelklick fügt Handler + Registrierung ein." };
        foreach (var h in api.Hooks.OrderBy(h => h.Register))
        {
            var node = new ApiNode { Label = h.Register, Documentation = api.FindGlobal(h.Register)?.ToMarkdownLikeText() ?? h.Register };
            var hookMatch = Match(h.Register);
            foreach (var e in h.Events)
            {
                if (!hookMatch && !Match(e.Name) && !Match(e.Id.ToString())) continue;
                node.Children.Add(new ApiNode
                {
                    Label = $"{e.Id,3}  {e.Name}",
                    Documentation = $"{e.Name} = {e.Id}\nRegistrierung: {h.Register}\nState: {e.State}\n\nHandler-Parameter:\n" +
                                    string.Join("\n", e.Params.Select(p => $"  {p.Name}: {p.Type}")) +
                                    (string.IsNullOrWhiteSpace(e.Comment) ? "" : "\n\n" + e.Comment),
                    InsertText = Core.Services.Snippets.EventSnippet(api, h, e),
                });
            }
            if (node.Children.Count > 0) events.Children.Add(node);
        }
        if (events.Children.Count > 0) roots.Add(events);

        foreach (var src in new[] { ApiSource.Aio, ApiSource.LuaStdLib })
        {
            var root = new ApiNode
            {
                Label = src == ApiSource.Aio ? "AIO" : "Lua-Standardbibliothek",
                Documentation = src == ApiSource.Aio ? "AIO – Server/Client-Kommunikation (https://github.com/Rochet2/AIO)" : "Lua 5.1 – 5.4 / LuaJIT Standardbibliothek.",
            };
            foreach (var lib in api.Libraries.Where(l => l.Source == src).OrderBy(l => l.Name))
            {
                var node = new ApiNode { Label = lib.Name, Documentation = lib.Description };
                var libMatch = Match(lib.Name);
                foreach (var fn in lib.Functions)
                {
                    if (libMatch || Match(fn.Name)) node.Children.Add(FromFunction(fn, $"{lib.Name}.{fn.Name}()"));
                }
                if (node.Children.Count > 0) root.Children.Add(node);
            }
            if (src == ApiSource.Aio && api.FindClass(ApiDatabase.AioMessageClass) is { } msg)
            {
                var node = new ApiNode { Label = msg.Name + " (AIO.Msg())", Documentation = msg.Description };
                foreach (var m in msg.Methods)
                {
                    if (Match(m.Name) || Match(msg.Name)) node.Children.Add(FromFunction(m, ":" + m.Name + "()"));
                }
                if (node.Children.Count > 0) root.Children.Add(node);
            }
            if (src == ApiSource.LuaStdLib)
            {
                foreach (var g in api.Globals.Where(g => g.Source == ApiSource.LuaStdLib).OrderBy(g => g.Name))
                {
                    if (Match(g.Name)) root.Children.Add(FromFunction(g, g.Name + "()"));
                }
            }
            if (root.Children.Count > 0) roots.Add(root);
        }
        return roots;
    }

    private static ApiNode FromFunction(ApiFunction f, string insert) => new()
    {
        Label = f.Name + "(" + string.Join(", ", f.Params.Select(p => p.IsVarArgs ? "..." : p.Name)) + ")" + (f.Returns.Count > 0 ? " → " + f.Returns[0].Type : ""),
        Documentation = f.ToMarkdownLikeText(),
        InsertText = insert,
    };
}

/// <summary>An entry of the error list.</summary>
public sealed class ProblemItem
{
    public required Diagnostic Diagnostic { get; init; }

    public string Icon => Diagnostic.Severity switch
    {
        DiagnosticSeverity.Error => "⛔",
        DiagnosticSeverity.Warning => "⚠",
        DiagnosticSeverity.Info => "ℹ",
        _ => "·",
    };

    public string FileName => Path.GetFileName(Diagnostic.FilePath ?? "");

    public override string ToString() =>
        $"{Icon}  {Diagnostic.Message.Split('\n')[0]}    — {FileName}:{Diagnostic.Line}:{Diagnostic.Column}  [{Diagnostic.Code}]{(Diagnostic.Fixes.Count > 0 ? "  💡" : "")}";
}

/// <summary>Per-user settings (recent projects, panel state).</summary>
public sealed class AppSettings
{
    public List<string> RecentProjects { get; set; } = new();
    public double FontSize { get; set; } = 14;
    public string? LastFolder { get; set; }

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ElunaWorkbench", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // corrupt settings – start fresh
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // ignore
        }
    }

    public void AddRecent(string projectFile)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, projectFile, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, projectFile);
        if (RecentProjects.Count > 10) RecentProjects.RemoveRange(10, RecentProjects.Count - 10);
        Save();
    }
}
