using System.Text.Json;
using System.Text.Json.Serialization;
using ElunaWorkbench.Core.Analysis;

namespace ElunaWorkbench.Core.Projects;

/// <summary>
/// An Eluna Workbench project (*.ewproj). The project file lives in the project root; all
/// Lua files below the root (except excluded folders) belong to the project.
/// </summary>
public sealed class ElunaProject
{
    public const string Extension = ".ewproj";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Name { get; set; } = "ElunaProjekt";

    /// <summary>Target emulator core, e.g. "TrinityCore". Null/empty = all cores.</summary>
    public string? TargetCore { get; set; } = "TrinityCore";

    public LuaVersion LuaVersion { get; set; } = LuaVersion.Lua52;

    /// <summary>Optional path of the server's lua_scripts folder for "Deploy".</summary>
    public string? DeployPath { get; set; }

    /// <summary>Folders (relative to the root) that should be ignored.</summary>
    public List<string> ExcludedFolders { get; set; } = new() { ".git", "bin", "obj", ".vs" };

    /// <summary>Folders (relative) whose files are treated as AIO client code.</summary>
    public List<string> ClientFolders { get; set; } = new() { "client" };

    public DiagnosticOptions Diagnostics { get; set; } = new();

    /// <summary>Files that were open when the project was closed (relative paths).</summary>
    public List<string> OpenFiles { get; set; } = new();

    [JsonIgnore] public string FilePath { get; private set; } = "";

    [JsonIgnore] public string RootDirectory => Path.GetDirectoryName(FilePath) ?? "";

    public static ElunaProject Load(string path)
    {
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<ElunaProject>(json, JsonOptions) ?? new ElunaProject();
        project.FilePath = Path.GetFullPath(path);
        return project;
    }

    public void Save()
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void SaveAs(string path)
    {
        FilePath = Path.GetFullPath(path);
        Save();
    }

    /// <summary>Creates a project in an (existing or new) folder from a template.</summary>
    public static ElunaProject Create(string directory, string name, ProjectTemplate template, string? core = "TrinityCore", LuaVersion version = LuaVersion.Lua52)
    {
        Directory.CreateDirectory(directory);
        var project = new ElunaProject { Name = name, TargetCore = core, LuaVersion = version };
        project.FilePath = Path.Combine(Path.GetFullPath(directory), SanitizeFileName(name) + Extension);
        ProjectTemplates.Apply(template, project.RootDirectory, name);
        project.Save();
        return project;
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "Projekt" : clean;
    }

    public bool IsExcluded(string fullPath)
    {
        var rel = Path.GetRelativePath(RootDirectory, fullPath).Replace('\\', '/');
        var parts = rel.Split('/');
        return parts.Any(p => ExcludedFolders.Any(e => string.Equals(e.Trim('/'), p, StringComparison.OrdinalIgnoreCase)));
    }

    public bool IsClientPath(string fullPath)
    {
        var rel = Path.GetRelativePath(RootDirectory, fullPath).Replace('\\', '/');
        return ClientFolders.Any(c => rel.StartsWith(c.Trim('/') + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>All Lua files of the project.</summary>
    public IEnumerable<string> EnumerateLuaFiles()
    {
        if (!Directory.Exists(RootDirectory)) yield break;
        var stack = new Stack<string>();
        stack.Push(RootDirectory);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.lua").ToList();
                dirs = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) yield return f;
            foreach (var d in dirs.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!IsExcluded(d)) stack.Push(d);
            }
        }
    }

    /// <summary>Copies all Lua files into the deploy folder (server lua_scripts).</summary>
    public int Deploy()
    {
        if (string.IsNullOrWhiteSpace(DeployPath)) throw new InvalidOperationException("Kein Deploy-Pfad konfiguriert.");
        var count = 0;
        foreach (var file in EnumerateLuaFiles())
        {
            var rel = Path.GetRelativePath(RootDirectory, file);
            var target = Path.Combine(DeployPath, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            count++;
        }
        return count;
    }
}
