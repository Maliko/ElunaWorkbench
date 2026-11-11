using ElunaWorkbench.Core.Api;

namespace ElunaWorkbench.Core.Analysis;

public enum LuaVersion
{
    Lua51,
    Lua52,
    Lua53,
    Lua54,
    LuaJIT,
}

public static class LuaVersionExtensions
{
    public static string DisplayName(this LuaVersion v) => v switch
    {
        LuaVersion.Lua51 => "Lua 5.1",
        LuaVersion.Lua52 => "Lua 5.2",
        LuaVersion.Lua53 => "Lua 5.3",
        LuaVersion.Lua54 => "Lua 5.4",
        LuaVersion.LuaJIT => "LuaJIT",
        _ => v.ToString(),
    };

    public static bool Is51Like(this LuaVersion v) => v is LuaVersion.Lua51 or LuaVersion.LuaJIT;
}

public sealed class DiagnosticOptions
{
    public bool ReportUndefinedGlobals { get; set; } = true;
    public bool ReportUnusedLocals { get; set; } = true;
    public bool ReportGlobalAssignmentInFunction { get; set; } = true;
    public bool ReportArgumentCount { get; set; } = true;
    public bool ReportCoreCompatibility { get; set; } = true;
    public bool InferTypesFromParameterNames { get; set; } = true;
}

/// <summary>Everything the analyzer needs to know beyond the file text.</summary>
public sealed class AnalysisContext
{
    public ApiDatabase Api { get; init; } = ApiDatabase.Default;

    public string FilePath { get; init; } = "untitled.lua";

    /// <summary>Target emulator core (e.g. "TrinityCore"); null = any core.</summary>
    public string? TargetCore { get; init; }

    public LuaVersion LuaVersion { get; init; } = LuaVersion.Lua52;

    public DiagnosticOptions Options { get; init; } = new();

    /// <summary>Globals defined in other project files.</summary>
    public Func<string, GlobalDefinition?> ProjectGlobals { get; init; } = _ => null;

    /// <summary>All global names defined in other project files (for completion).</summary>
    public Func<IEnumerable<GlobalDefinition>> AllProjectGlobals { get; init; } = Enumerable.Empty<GlobalDefinition>;

    /// <summary>AIO registrations of other project files: (name, clientSide) → merged registration.</summary>
    public Func<string, bool, AioRegistration?> AioRegistrations { get; init; } = (_, _) => null;

    /// <summary>Force client (WoW addon) / server mode; null = auto-detect.</summary>
    public bool? IsClientFile { get; init; }
}
