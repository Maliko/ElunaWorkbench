using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ElunaWorkbench.Core.Api;

/// <summary>
/// Central registry of everything the editor knows about the Eluna API, AIO,
/// the Lua standard library and a small part of the WoW client API.
/// </summary>
public sealed class ApiDatabase
{
    private static readonly Lazy<ApiDatabase> _default = new(LoadDefault);

    private readonly Dictionary<string, ApiClass> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApiFunction> _globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApiField> _globalFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApiLibrary> _libraries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApiHook> _hooks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ApiFunction>> _methodCache = new(StringComparer.Ordinal);

    public static ApiDatabase Default => _default.Value;

    /// <summary>Class name used for AIO messages (AIO.Msg()).</summary>
    public const string AioMessageClass = BuiltinApi.AioMessageClass;

    public IReadOnlyList<string> Cores { get; private set; } = Array.Empty<string>();
    public IEnumerable<ApiClass> Classes => _classes.Values;
    public IEnumerable<ApiFunction> Globals => _globals.Values;
    public IEnumerable<ApiField> GlobalFields => _globalFields.Values;
    public IEnumerable<ApiLibrary> Libraries => _libraries.Values;
    public IEnumerable<ApiHook> Hooks => _hooks.Values;

    /// <summary>Type aliases used in the Eluna documentation.</summary>
    private static readonly Dictionary<string, string> TypeAliases = new(StringComparer.Ordinal)
    {
        ["SpellInfo"] = "ElunaSpellInfo",
    };

    private static ApiDatabase LoadDefault()
    {
        var db = new ApiDatabase();
        var asm = typeof(ApiDatabase).Assembly;
        using (var stream = asm.GetManifestResourceStream("ElunaWorkbench.Core.eluna_api.json"))
        {
            if (stream != null)
            {
                db.LoadElunaJson(stream);
            }
        }
        db.LoadBuiltins();
        return db;
    }

    public void LoadElunaJson(Stream stream)
    {
        var file = JsonSerializer.Deserialize<ElunaApiFile>(stream) ?? new ElunaApiFile();
        Cores = file.Cores;
        foreach (var cls in file.Classes)
        {
            cls.Source = ApiSource.Eluna;
            foreach (var m in cls.Methods)
            {
                m.Owner = cls.Name;
                m.IsMethod = true;
                m.Source = ApiSource.Eluna;
                NormalizeTypes(m);
            }
            _classes[cls.Name] = cls;
        }
        foreach (var g in file.Globals)
        {
            g.Source = ApiSource.Eluna;
            NormalizeTypes(g);
            _globals[g.Name] = g;
        }
        foreach (var h in file.Hooks)
        {
            if (_globals.TryGetValue(h.Register, out var fn))
            {
                h.EventArgIndex = Math.Max(0, fn.Params.FindIndex(p => p.Name == "event"));
                h.HandlerArgIndex = Math.Max(0, fn.Params.FindIndex(p => p.Type == "function"));
            }
            foreach (var e in h.Events)
            {
                foreach (var p in e.Params)
                {
                    p.Type = NormalizeType(p.Type);
                }
            }
            _hooks[h.Register] = h;
        }
        _methodCache.Clear();
    }

    private static void NormalizeTypes(ApiFunction f)
    {
        foreach (var p in f.Params) p.Type = NormalizeType(p.Type);
        foreach (var r in f.Returns) r.Type = NormalizeType(r.Type);
    }

    private static string NormalizeType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "any";
        type = type.Trim().Trim('[', ']');
        return TypeAliases.TryGetValue(type, out var alias) ? alias : type;
    }

    private void LoadBuiltins()
    {
        foreach (var sig in BuiltinApi.LuaGlobals)
        {
            var f = ParseSignature(sig, null, false, ApiSource.LuaStdLib);
            _globals.TryAdd(f.Name, f);
        }
        foreach (var (name, desc, funcs) in BuiltinApi.LuaLibraries)
        {
            var lib = GetOrCreateLibrary(name, desc, ApiSource.LuaStdLib);
            foreach (var sig in funcs) lib.Functions.Add(ParseSignature(sig, name, false, ApiSource.LuaStdLib));
        }
        foreach (var (libName, name, type, desc) in BuiltinApi.LuaFields)
        {
            var field = new ApiField { Name = name, Type = type, Description = desc, Source = ApiSource.LuaStdLib };
            if (libName.Length == 0) _globalFields[name] = field;
            else GetOrCreateLibrary(libName, "", ApiSource.LuaStdLib).Fields.Add(field);
        }
        var searchPath = ParseSignature("searchpath(name: string, path: string, sep?: string, rep?: string): string | Lua 5.2+: searches for the given name in the given path.", "package", false, ApiSource.LuaStdLib);
        GetOrCreateLibrary("package", "Module/package facilities.", ApiSource.LuaStdLib).Functions.Add(searchPath);

        // AIO
        var aio = GetOrCreateLibrary("AIO", "AIO – server/client communication and addon delivery for Eluna (https://github.com/Rochet2/AIO).", ApiSource.Aio);
        foreach (var sig in BuiltinApi.AioFunctions) aio.Functions.Add(ParseSignature(sig, "AIO", false, ApiSource.Aio));
        var msgClass = new ApiClass
        {
            Name = BuiltinApi.AioMessageClass,
            Description = "AIO message created with AIO.Msg(). Methods can be chained: AIO.Msg():Add(\"Name\", ...):Send(player)",
            Source = ApiSource.Aio,
        };
        foreach (var sig in BuiltinApi.AioMessageMethods) msgClass.Methods.Add(ParseSignature(sig, BuiltinApi.AioMessageClass, true, ApiSource.Aio));
        _classes[msgClass.Name] = msgClass;

        // WoW client API (subset) – used in AIO client files.
        foreach (var sig in BuiltinApi.WowClientGlobals)
        {
            var f = ParseSignature(sig, null, false, ApiSource.WowClient);
            _globals.TryAdd(f.Name, f);
        }
        foreach (var (name, type, desc) in BuiltinApi.WowClientFields)
        {
            _globalFields.TryAdd(name, new ApiField { Name = name, Type = type, Description = desc, Source = ApiSource.WowClient });
        }
    }

    private ApiLibrary GetOrCreateLibrary(string name, string desc, ApiSource source)
    {
        if (!_libraries.TryGetValue(name, out var lib))
        {
            lib = new ApiLibrary { Name = name, Description = desc, Source = source };
            _libraries[name] = lib;
        }
        return lib;
    }

    private static readonly Regex SignatureRegex = new(@"^(?<name>\w+)\((?<params>[^)]*)\)(?:\s*:\s*(?<ret>[\w|]+))?\s*(?:\|\s*(?<desc>.*))?$", RegexOptions.Compiled);

    internal static ApiFunction ParseSignature(string sig, string? owner, bool isMethod, ApiSource source)
    {
        var m = SignatureRegex.Match(sig);
        if (!m.Success) throw new FormatException("Invalid builtin signature: " + sig);
        var f = new ApiFunction
        {
            Name = m.Groups["name"].Value,
            Owner = owner,
            IsMethod = isMethod,
            Source = source,
            Description = m.Groups["desc"].Value.Trim(),
        };
        foreach (var raw in m.Groups["params"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "...")
            {
                f.Params.Add(new ApiParameter { Name = "...", Type = "..." });
                continue;
            }
            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            var name = parts[0];
            var optional = name.EndsWith('?');
            if (optional) name = name.TrimEnd('?');
            f.Params.Add(new ApiParameter
            {
                Name = name,
                Type = parts.Length > 1 ? parts[1] : "any",
                Default = optional ? "nil" : null,
            });
        }
        if (m.Groups["ret"].Success)
        {
            f.Returns.Add(new ApiParameter { Name = "result", Type = m.Groups["ret"].Value });
        }
        return f;
    }

    public bool IsClass(string? name) => name != null && _classes.ContainsKey(name);

    public ApiClass? FindClass(string? name) => name != null && _classes.TryGetValue(name, out var c) ? c : null;

    public ApiFunction? FindGlobal(string name) => _globals.TryGetValue(name, out var f) ? f : null;

    public ApiField? FindGlobalField(string name) => _globalFields.TryGetValue(name, out var f) ? f : null;

    public ApiLibrary? FindLibrary(string name) => _libraries.TryGetValue(name, out var l) ? l : null;

    public ApiHook? FindHook(string registerFunction) => _hooks.TryGetValue(registerFunction, out var h) ? h : null;

    /// <summary>True if the name is a builtin global (function, field or library).</summary>
    public bool IsKnownGlobal(string name) =>
        _globals.ContainsKey(name) || _globalFields.ContainsKey(name) || _libraries.ContainsKey(name);

    /// <summary>Returns all methods of a class including inherited ones (most derived first).</summary>
    public IReadOnlyList<ApiFunction> GetMethods(string className)
    {
        if (_methodCache.TryGetValue(className, out var cached)) return cached;
        var result = new List<ApiFunction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (_classes.TryGetValue(className, out var cls))
        {
            foreach (var m in cls.Methods)
            {
                if (seen.Add(m.Name)) result.Add(m);
            }
            foreach (var baseName in cls.Inherits.AsEnumerable().Reverse())
            {
                if (!_classes.TryGetValue(baseName, out var baseCls)) continue;
                foreach (var m in baseCls.Methods)
                {
                    if (seen.Add(m.Name)) result.Add(m);
                }
            }
        }
        _methodCache[className] = result;
        return result;
    }

    /// <summary>Methods for a possibly union type such as "Creature|GameObject|Item".</summary>
    public IReadOnlyList<ApiFunction> GetMethodsForType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return Array.Empty<ApiFunction>();
        if (!type.Contains('|')) return GetMethods(type);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ApiFunction>();
        foreach (var part in SplitUnion(type))
        {
            foreach (var m in GetMethods(part))
            {
                if (seen.Add(m.Name)) result.Add(m);
            }
        }
        return result;
    }

    public ApiFunction? FindMethod(string? type, string name)
    {
        if (string.IsNullOrEmpty(type)) return null;
        foreach (var part in SplitUnion(type))
        {
            var m = GetMethods(part).FirstOrDefault(f => f.Name == name);
            if (m != null) return m;
        }
        return null;
    }

    /// <summary>True when every part of the (union) type is a known class.</summary>
    public bool IsKnownObjectType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        return SplitUnion(type).All(IsClass);
    }

    public static IEnumerable<string> SplitUnion(string type) =>
        type.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Maps a documentation type (uint32, float, bool, ...) to a Lua type name.
    /// Returns the class name for Eluna objects.
    /// </summary>
    public string ToLuaType(string? docType)
    {
        if (string.IsNullOrEmpty(docType)) return "any";
        if (docType.Contains('|'))
        {
            var parts = SplitUnion(docType).Select(ToLuaType).Distinct().ToList();
            return string.Join("|", parts);
        }
        if (IsClass(docType)) return docType;
        switch (docType)
        {
            case "bool":
            case "boolean":
                return "boolean";
            case "string":
            case "const char*":
                return "string";
            case "table":
                return "table";
            case "function":
                return "function";
            case "nil":
                return "nil";
            case "ObjectGuid":
            case "uint64":
            case "int64":
                return "number";
        }
        if (Regex.IsMatch(docType, @"^(u?int\d*|float|double|number|int|long|short)$")) return "number";
        // Enum-ish types (Powers, Team, ...) are numbers in Lua.
        if (char.IsUpper(docType[0])) return "number";
        return "any";
    }

    /// <summary>All event-registration hooks that can be used with a given method name.</summary>
    public IEnumerable<ApiEvent> AllEvents => _hooks.Values.SelectMany(h => h.Events);
}
