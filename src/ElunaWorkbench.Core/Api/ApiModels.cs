using System.Text;
using System.Text.Json.Serialization;

namespace ElunaWorkbench.Core.Api;

/// <summary>Where an API entry comes from.</summary>
public enum ApiSource
{
    Eluna,
    Aio,
    LuaStdLib,
    WowClient,
}

public sealed class ApiParameter
{
    [JsonPropertyName("type")] public string Type { get; set; } = "any";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("default")] public string? Default { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    [JsonIgnore] public bool IsVarArgs => Name == "..." || Type == "...";
    [JsonIgnore] public bool IsOptional => Default != null || IsVarArgs;

    public override string ToString()
    {
        if (IsVarArgs) return "...";
        var s = $"{Type} {Name}";
        if (Default != null) s += " = " + Default;
        return s;
    }
}

public sealed class ApiFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("params")] public List<ApiParameter> Params { get; set; } = new();
    [JsonPropertyName("returns")] public List<ApiParameter> Returns { get; set; } = new();
    [JsonPropertyName("protos")] public List<string> Protos { get; set; } = new();
    [JsonPropertyName("cores")] public List<string> Cores { get; set; } = new();

    /// <summary>Class name (or library/table name) that owns the function. Null for plain globals.</summary>
    [JsonIgnore] public string? Owner { get; set; }

    /// <summary>True when the function is called with ':' (methods of Eluna objects, AIO messages).</summary>
    [JsonIgnore] public bool IsMethod { get; set; }

    [JsonIgnore] public ApiSource Source { get; set; } = ApiSource.Eluna;

    /// <summary>The first return type (or null).</summary>
    [JsonIgnore] public string? ReturnType => Returns.Count > 0 ? Returns[0].Type : null;

    [JsonIgnore]
    public string QualifiedName => Owner == null ? Name : Owner + (IsMethod ? ":" : ".") + Name;

    /// <summary>Minimum number of arguments a call must provide.</summary>
    [JsonIgnore]
    public int MinArgs
    {
        get
        {
            var protoCounts = ProtoArgCounts();
            if (protoCounts.Count > 0) return protoCounts.Min();
            var n = 0;
            foreach (var p in Params)
            {
                if (p.IsOptional) break;
                n++;
            }
            return n;
        }
    }

    /// <summary>Maximum number of arguments, or int.MaxValue for variadic functions.</summary>
    [JsonIgnore]
    public int MaxArgs
    {
        get
        {
            if (Params.Any(p => p.IsVarArgs)) return int.MaxValue;
            var protoCounts = ProtoArgCounts();
            var max = Params.Count;
            if (protoCounts.Count > 0) max = Math.Max(max, protoCounts.Max());
            return max;
        }
    }

    private List<int> ProtoArgCounts()
    {
        var result = new List<int>();
        foreach (var proto in Protos)
        {
            var open = proto.IndexOf('(');
            var close = proto.LastIndexOf(')');
            if (open < 0 || close < open) continue;
            var inner = proto.Substring(open + 1, close - open - 1).Trim();
            if (inner.Contains("...")) return new List<int>();
            result.Add(inner.Length == 0 ? 0 : inner.Split(',').Length);
        }
        return result;
    }

    public string Signature
    {
        get
        {
            var sb = new StringBuilder();
            if (Returns.Count > 0)
            {
                sb.Append(string.Join(", ", Returns.Select(r => r.Type)));
                sb.Append(' ');
            }
            sb.Append(QualifiedName);
            sb.Append('(');
            sb.Append(string.Join(", ", Params.Select(p => p.ToString())));
            sb.Append(')');
            return sb.ToString();
        }
    }

    public string ToMarkdownLikeText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Signature);
        if (!string.IsNullOrWhiteSpace(Description))
        {
            sb.AppendLine();
            sb.AppendLine(ApiText.StripLinks(Description));
        }
        if (Params.Count > 0)
        {
            sb.AppendLine();
            foreach (var p in Params)
            {
                sb.Append("  • ").Append(p.IsVarArgs ? "..." : $"{p.Name} ({p.Type})");
                if (p.Default != null) sb.Append(" = ").Append(p.Default);
                if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append(": ").Append(ApiText.StripLinks(p.Description));
                sb.AppendLine();
            }
        }
        if (Returns.Count > 0)
        {
            sb.Append("  ↳ Rückgabe: ").AppendLine(string.Join(", ", Returns.Select(r => $"{r.Name} ({r.Type})")));
        }
        if (Cores.Count > 0 && Source == ApiSource.Eluna)
        {
            sb.Append("  Cores: ").AppendLine(string.Join(", ", Cores));
        }
        return sb.ToString().TrimEnd();
    }
}

public sealed class ApiClass
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("inherits")] public List<string> Inherits { get; set; } = new();
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("methods")] public List<ApiFunction> Methods { get; set; } = new();

    [JsonIgnore] public ApiSource Source { get; set; } = ApiSource.Eluna;
}

public sealed class ApiEventParameter
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "any";
}

public sealed class ApiEvent
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("params")] public List<ApiEventParameter> Params { get; set; } = new();
    [JsonPropertyName("comment")] public string Comment { get; set; } = "";

    public string ParameterList => string.Join(", ", Params.Select(p => p.Name));
}

public sealed class ApiHook
{
    [JsonPropertyName("register")] public string Register { get; set; } = "";
    [JsonPropertyName("hook")] public string Hook { get; set; } = "";
    [JsonPropertyName("events")] public List<ApiEvent> Events { get; set; } = new();

    /// <summary>
    /// Index of the event id argument and of the handler function argument in the
    /// Register*Event call. E.g. RegisterCreatureEvent(entry, event, function).
    /// </summary>
    [JsonIgnore] public int EventArgIndex { get; set; }
    [JsonIgnore] public int HandlerArgIndex { get; set; } = 1;

    public ApiEvent? FindEvent(int id) => Events.FirstOrDefault(e => e.Id == id);
}

/// <summary>A library table such as <c>string</c>, <c>math</c> or <c>AIO</c>.</summary>
public sealed class ApiLibrary
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public ApiSource Source { get; init; }
    public List<ApiFunction> Functions { get; } = new();
    public List<ApiField> Fields { get; } = new();
}

public sealed class ApiField
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "any";
    public string Description { get; init; } = "";
    public ApiSource Source { get; init; }
}

internal sealed class ElunaApiFile
{
    [JsonPropertyName("cores")] public List<string> Cores { get; set; } = new();
    [JsonPropertyName("classes")] public List<ApiClass> Classes { get; set; } = new();
    [JsonPropertyName("globals")] public List<ApiFunction> Globals { get; set; } = new();
    [JsonPropertyName("hooks")] public List<ApiHook> Hooks { get; set; } = new();
}

public static class ApiText
{
    /// <summary>Turns ElunaDoc links like "[Player]" into plain words.</summary>
    public static string StripLinks(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close > i + 1 && close - i < 40 && text.Substring(i + 1, close - i - 1).All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                {
                    sb.Append(text, i + 1, close - i - 1);
                    i = close;
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
