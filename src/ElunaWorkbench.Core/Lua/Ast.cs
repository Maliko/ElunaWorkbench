namespace ElunaWorkbench.Core.Lua;

/// <summary>Base type of all syntax nodes. Offsets are character offsets into the source.</summary>
public abstract class Node
{
    public int Start { get; set; }
    public int End { get; set; }
    public int Length => End - Start;

    public bool Contains(int offset) => offset >= Start && offset <= End;
}

public sealed class Identifier : Node
{
    public Identifier(string name, int start, int length)
    {
        Name = name;
        Start = start;
        End = start + length;
    }

    public string Name { get; }

    /// <summary>Lua 5.4 attribute (&lt;const&gt; / &lt;close&gt;), if any.</summary>
    public string? Attribute { get; set; }

    public override string ToString() => Name;
}

// ---------------------------------------------------------------------------------------------
// Expressions
// ---------------------------------------------------------------------------------------------

public abstract class Expr : Node
{
}

public enum LiteralKind
{
    Nil,
    True,
    False,
    Number,
    String,
    VarArgs,
}

public sealed class LiteralExpr : Expr
{
    public LiteralKind Kind { get; init; }
    public string Text { get; init; } = "";

    /// <summary>Unescaped string value for strings.</summary>
    public string? StringValue { get; init; }

    public double? NumberValue
    {
        get
        {
            if (Kind != LiteralKind.Number) return null;
            var t = Text.TrimEnd('U', 'L', 'u', 'l', 'i');
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return long.TryParse(t[2..], System.Globalization.NumberStyles.HexNumber, null, out var h) ? h : null;
            }
            return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
        }
    }
}

public sealed class NameExpr : Expr
{
    public NameExpr(Identifier id)
    {
        Id = id;
        Start = id.Start;
        End = id.End;
    }

    public Identifier Id { get; }
    public string Name => Id.Name;
}

/// <summary>a.b</summary>
public sealed class MemberExpr : Expr
{
    public required Expr Object { get; init; }
    public required Identifier Member { get; init; }
}

/// <summary>a[b]</summary>
public sealed class IndexExpr : Expr
{
    public required Expr Object { get; init; }
    public required Expr Key { get; init; }
}

/// <summary>f(args)</summary>
public sealed class CallExpr : Expr
{
    public required Expr Callee { get; init; }
    public List<Expr> Args { get; } = new();

    /// <summary>Offset of '(' (or of the string/table argument).</summary>
    public int ArgsStart { get; set; }
}

/// <summary>obj:name(args)</summary>
public sealed class MethodCallExpr : Expr
{
    public required Expr Object { get; init; }
    public required Identifier Method { get; init; }
    public List<Expr> Args { get; } = new();
    public int ArgsStart { get; set; }
}

public sealed class FunctionExpr : Expr
{
    public List<Identifier> Params { get; } = new();
    public bool IsVarArg { get; set; }
    public Block Body { get; set; } = new();

    /// <summary>True for "function a:b()" – implicit self parameter.</summary>
    public bool HasImplicitSelf { get; set; }

    /// <summary>Human readable name, for outline/diagnostics.</summary>
    public string? DisplayName { get; set; }
}

public sealed class TableField : Node
{
    /// <summary>Name for "name = value" fields.</summary>
    public Identifier? Name { get; init; }

    /// <summary>Key expression for "[key] = value" fields.</summary>
    public Expr? Key { get; init; }

    public required Expr Value { get; init; }
}

public sealed class TableExpr : Expr
{
    public List<TableField> Fields { get; } = new();
}

public sealed class BinaryExpr : Expr
{
    public required string Operator { get; init; }
    public required Expr Left { get; init; }
    public required Expr Right { get; init; }
}

public sealed class UnaryExpr : Expr
{
    public required string Operator { get; init; }
    public required Expr Operand { get; init; }
}

public sealed class ParenExpr : Expr
{
    public required Expr Inner { get; init; }
}

public sealed class ErrorExpr : Expr
{
}

// ---------------------------------------------------------------------------------------------
// Statements
// ---------------------------------------------------------------------------------------------

public abstract class Stmt : Node
{
}

public sealed class Block : Node
{
    public List<Stmt> Statements { get; } = new();
}

public sealed class LocalStmt : Stmt
{
    public List<Identifier> Names { get; } = new();
    public List<Expr> Values { get; } = new();
}

public sealed class AssignStmt : Stmt
{
    public List<Expr> Targets { get; } = new();
    public List<Expr> Values { get; } = new();
}

public sealed class CallStmt : Stmt
{
    public required Expr Call { get; init; }
}

public sealed class DoStmt : Stmt
{
    public Block Body { get; set; } = new();
}

public sealed class WhileStmt : Stmt
{
    public required Expr Condition { get; init; }
    public Block Body { get; set; } = new();
}

public sealed class RepeatStmt : Stmt
{
    public Block Body { get; set; } = new();
    public Expr Condition { get; set; } = new ErrorExpr();
}

public sealed class IfClause : Node
{
    public required Expr Condition { get; init; }
    public Block Body { get; set; } = new();
}

public sealed class IfStmt : Stmt
{
    public List<IfClause> Clauses { get; } = new();
    public Block? Else { get; set; }
}

public sealed class NumericForStmt : Stmt
{
    public required Identifier Variable { get; init; }
    public required Expr From { get; init; }
    public required Expr To { get; init; }
    public Expr? Step { get; init; }
    public Block Body { get; set; } = new();
}

public sealed class GenericForStmt : Stmt
{
    public List<Identifier> Names { get; } = new();
    public List<Expr> Iterators { get; } = new();
    public Block Body { get; set; } = new();
}

/// <summary>function a.b.c:d() ... end</summary>
public sealed class FunctionStmt : Stmt
{
    /// <summary>The target: NameExpr or MemberExpr chain.</summary>
    public required Expr Target { get; init; }

    /// <summary>Method name for "a:b" functions.</summary>
    public Identifier? MethodName { get; init; }

    public required FunctionExpr Function { get; init; }
}

public sealed class LocalFunctionStmt : Stmt
{
    public required Identifier Name { get; init; }
    public required FunctionExpr Function { get; init; }
}

public sealed class ReturnStmt : Stmt
{
    public List<Expr> Values { get; } = new();
}

public sealed class BreakStmt : Stmt
{
}

public sealed class GotoStmt : Stmt
{
    public required Identifier Label { get; init; }
}

public sealed class LabelStmt : Stmt
{
    public required Identifier Label { get; init; }
}

public sealed class Chunk : Node
{
    public Block Body { get; set; } = new();
}

/// <summary>Syntax features that are not available in every Lua version.</summary>
public enum LuaFeature
{
    Goto,
    IntegerDivision,
    BitwiseOperators,
    Attributes,
}

public readonly record struct FeatureUse(LuaFeature Feature, int Start, int Length);
