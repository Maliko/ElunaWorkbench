using ElunaWorkbench.Core.Analysis;
using ElunaWorkbench.Core.Api;
using ElunaWorkbench.Core.Lua;

namespace ElunaWorkbench.Core.Projects;

/// <summary>
/// Keeps the analysis results of all project files and provides cross-file knowledge
/// (globals defined in other files, AIO handler registrations).
/// Thread-safe: all members can be called from background threads.
/// </summary>
public sealed class Workspace
{
    private readonly object _lock = new();
    private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);

    public Workspace(ElunaProject? project = null, ApiDatabase? api = null)
    {
        Project = project;
        Api = api ?? ApiDatabase.Default;
    }

    public ElunaProject? Project { get; }
    public ApiDatabase Api { get; }

    /// <summary>Raised after a file was (re-)analysed. Argument: file path.</summary>
    public event Action<string>? FileAnalyzed;

    private sealed class FileState
    {
        public string Text = "";
        public SemanticModel? Model;
    }

    public IReadOnlyList<string> Files
    {
        get
        {
            lock (_lock) return _files.Keys.ToList();
        }
    }

    public static string Normalize(string path) => Path.GetFullPath(path);

    /// <summary>Loads and analyses all Lua files of the project.</summary>
    public void LoadProjectFiles()
    {
        if (Project == null) return;
        foreach (var file in Project.EnumerateLuaFiles())
        {
            try
            {
                SetText(file, File.ReadAllText(file), analyze: false);
            }
            catch (IOException)
            {
                // ignore unreadable files
            }
        }
        // Two rounds: the first collects globals of all files, the second reports diagnostics
        // with complete cross-file knowledge.
        AnalyzeAll();
        AnalyzeAll();
    }

    public void AnalyzeAll()
    {
        foreach (var f in Files) Analyze(f);
    }

    public void SetText(string path, string text, bool analyze = true)
    {
        path = Normalize(path);
        lock (_lock)
        {
            if (!_files.TryGetValue(path, out var st))
            {
                st = new FileState();
                _files[path] = st;
            }
            st.Text = text;
        }
        if (analyze) Analyze(path);
    }

    public void RemoveFile(string path)
    {
        path = Normalize(path);
        lock (_lock) _files.Remove(path);
    }

    public void RenameFile(string oldPath, string newPath)
    {
        oldPath = Normalize(oldPath);
        newPath = Normalize(newPath);
        lock (_lock)
        {
            if (_files.Remove(oldPath, out var st)) _files[newPath] = st;
        }
        Analyze(newPath);
    }

    public string? GetText(string path)
    {
        lock (_lock) return _files.TryGetValue(Normalize(path), out var st) ? st.Text : null;
    }

    public SemanticModel? GetModel(string path)
    {
        lock (_lock) return _files.TryGetValue(Normalize(path), out var st) ? st.Model : null;
    }

    /// <summary>Analyses one file and stores the result. Returns the model.</summary>
    public SemanticModel Analyze(string path)
    {
        path = Normalize(path);
        string text;
        lock (_lock)
        {
            text = _files.TryGetValue(path, out var st) ? st.Text : "";
        }
        var model = Analyzer.Analyze(text, CreateContext(path));
        lock (_lock)
        {
            if (_files.TryGetValue(path, out var st)) st.Model = model;
        }
        FileAnalyzed?.Invoke(path);
        return model;
    }

    /// <summary>Analyses a text for a path without storing the result (e.g. for completion).</summary>
    public SemanticModel AnalyzeText(string path, string text) => Analyzer.Analyze(text, CreateContext(Normalize(path)));

    public AnalysisContext CreateContext(string path)
    {
        path = Normalize(path);
        bool? isClient = null;
        if (Project != null && Project.IsClientPath(path)) isClient = true;
        return new AnalysisContext
        {
            Api = Api,
            FilePath = path,
            TargetCore = string.IsNullOrWhiteSpace(Project?.TargetCore) ? null : Project!.TargetCore,
            LuaVersion = Project?.LuaVersion ?? LuaVersion.Lua52,
            Options = Project?.Diagnostics ?? new DiagnosticOptions(),
            IsClientFile = isClient,
            ProjectGlobals = name => FindGlobal(name, excludePath: path),
            AllProjectGlobals = () => AllGlobals(excludePath: path),
            AioRegistrations = (name, client) => FindAio(name, client, excludePath: null),
        };
    }

    private List<SemanticModel> ModelsExcept(string? excludePath)
    {
        lock (_lock)
        {
            return _files
                .Where(kv => kv.Value.Model != null && (excludePath == null || !string.Equals(kv.Key, excludePath, StringComparison.OrdinalIgnoreCase)))
                .Select(kv => kv.Value.Model!)
                .ToList();
        }
    }

    public GlobalDefinition? FindGlobal(string name, string? excludePath = null)
    {
        var defs = ModelsExcept(excludePath)
            .Select(m => m.GlobalDefinitions.TryGetValue(name, out var g) ? g : null)
            .Where(g => g != null)
            .Select(g => g!)
            .ToList();
        if (defs.Count == 0) return null;
        if (defs.Count == 1) return defs[0];
        // Prefer a real definition (function / assigned value) over a pure field write and merge fields.
        var primary = defs.FirstOrDefault(g => g.Function != null || g.Type != null) ?? defs[0];
        var merged = new GlobalDefinition { Name = name, FilePath = primary.FilePath, Offset = primary.Offset, Type = primary.Type, Function = primary.Function };
        foreach (var g in defs)
        {
            foreach (var kv in g.Fields) merged.Fields.TryAdd(kv.Key, kv.Value);
        }
        return merged;
    }

    public IEnumerable<GlobalDefinition> AllGlobals(string? excludePath = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in ModelsExcept(excludePath))
        {
            foreach (var g in m.GlobalDefinitions.Values)
            {
                if (seen.Add(g.Name)) yield return g;
            }
        }
    }

    public AioRegistration? FindAio(string name, bool clientSide, string? excludePath = null)
    {
        AioRegistration? merged = null;
        foreach (var m in ModelsExcept(excludePath))
        {
            foreach (var r in m.AioRegistrations.Where(r => r.Name == name && r.IsClient == clientSide))
            {
                merged ??= new AioRegistration { Name = name, FilePath = r.FilePath, Offset = r.Offset, IsClient = clientSide };
                merged.HandlerNames.UnionWith(r.HandlerNames);
                if (!r.HandlersKnown) merged.HandlersKnown = false;
            }
        }
        return merged;
    }

    public IEnumerable<AioRegistration> AllAioRegistrations()
    {
        foreach (var m in ModelsExcept(null))
        {
            foreach (var r in m.AioRegistrations) yield return r;
        }
    }

    /// <summary>All diagnostics of all files.</summary>
    public List<Diagnostic> AllDiagnostics()
    {
        return ModelsExcept(null).SelectMany(m => m.Diagnostics).ToList();
    }
}
