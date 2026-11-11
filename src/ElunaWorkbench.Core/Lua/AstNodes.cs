namespace ElunaWorkbench.Core.Lua;

/// <summary>Generic helpers to traverse the syntax tree.</summary>
public static class AstNodes
{
    public static IEnumerable<Node> Children(Node node)
    {
        switch (node)
        {
            case Chunk c:
                yield return c.Body;
                break;
            case Block b:
                foreach (var s in b.Statements) yield return s;
                break;
            case LocalStmt l:
                foreach (var n in l.Names) yield return n;
                foreach (var v in l.Values) yield return v;
                break;
            case AssignStmt a:
                foreach (var t in a.Targets) yield return t;
                foreach (var v in a.Values) yield return v;
                break;
            case CallStmt cs:
                yield return cs.Call;
                break;
            case DoStmt d:
                yield return d.Body;
                break;
            case WhileStmt w:
                yield return w.Condition;
                yield return w.Body;
                break;
            case RepeatStmt r:
                yield return r.Body;
                yield return r.Condition;
                break;
            case IfStmt i:
                foreach (var cl in i.Clauses)
                {
                    yield return cl.Condition;
                    yield return cl.Body;
                }
                if (i.Else != null) yield return i.Else;
                break;
            case NumericForStmt nf:
                yield return nf.Variable;
                yield return nf.From;
                yield return nf.To;
                if (nf.Step != null) yield return nf.Step;
                yield return nf.Body;
                break;
            case GenericForStmt gf:
                foreach (var n in gf.Names) yield return n;
                foreach (var it in gf.Iterators) yield return it;
                yield return gf.Body;
                break;
            case FunctionStmt fs:
                yield return fs.Target;
                if (fs.MethodName != null) yield return fs.MethodName;
                yield return fs.Function;
                break;
            case LocalFunctionStmt lf:
                yield return lf.Name;
                yield return lf.Function;
                break;
            case ReturnStmt rs:
                foreach (var v in rs.Values) yield return v;
                break;
            case GotoStmt g:
                yield return g.Label;
                break;
            case LabelStmt ls:
                yield return ls.Label;
                break;
            case MemberExpr m:
                yield return m.Object;
                yield return m.Member;
                break;
            case IndexExpr ix:
                yield return ix.Object;
                yield return ix.Key;
                break;
            case CallExpr call:
                yield return call.Callee;
                foreach (var a in call.Args) yield return a;
                break;
            case MethodCallExpr mc:
                yield return mc.Object;
                yield return mc.Method;
                foreach (var a in mc.Args) yield return a;
                break;
            case FunctionExpr f:
                foreach (var p in f.Params) yield return p;
                yield return f.Body;
                break;
            case TableExpr t:
                foreach (var field in t.Fields) yield return field;
                break;
            case TableField tf:
                if (tf.Name != null) yield return tf.Name;
                if (tf.Key != null) yield return tf.Key;
                yield return tf.Value;
                break;
            case BinaryExpr be:
                yield return be.Left;
                yield return be.Right;
                break;
            case UnaryExpr ue:
                yield return ue.Operand;
                break;
            case ParenExpr pe:
                yield return pe.Inner;
                break;
        }
    }

    /// <summary>All nodes in pre-order.</summary>
    public static IEnumerable<Node> Descendants(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            var children = Children(n).ToList();
            for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
        }
    }

    /// <summary>The path from the root to the deepest node containing the offset.</summary>
    public static List<Node> PathTo(Node root, int offset)
    {
        var path = new List<Node>();
        var current = root;
        while (true)
        {
            path.Add(current);
            Node? next = null;
            foreach (var child in Children(current))
            {
                if (offset >= child.Start && offset <= child.End && child.End > child.Start)
                {
                    next = child;
                }
            }
            if (next == null) break;
            current = next;
        }
        return path;
    }
}
