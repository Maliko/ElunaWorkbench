using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using ElunaWorkbench.Core.Api;
using ElunaWorkbench.Core.Services;

namespace ElunaWorkbench.Editor;

/// <summary>Adapter between <see cref="CompletionItem"/> and AvaloniaEdit's completion list.</summary>
public sealed class CompletionData : ICompletionData
{
    private readonly CompletionItem _item;
    private readonly string _indentUnit;

    public CompletionData(CompletionItem item, double priority, string indentUnit)
    {
        _item = item;
        Priority = priority;
        _indentUnit = indentUnit;
    }

    public Avalonia.Media.IImage? Image => null;

    public string Text => _item.Label;

    public object Content
    {
        get
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(new Border
            {
                Width = 18,
                Height = 16,
                CornerRadius = new Avalonia.CornerRadius(3),
                Background = new SolidColorBrush(KindColor(_item.Kind)),
                Child = new TextBlock
                {
                    Text = KindLetter(_item.Kind),
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
            var label = new TextBlock { Text = _item.Label, VerticalAlignment = VerticalAlignment.Center };
            if (_item.Unavailable)
            {
                label.Foreground = Brushes.Gray;
                label.TextDecorations = TextDecorations.Strikethrough;
            }
            panel.Children.Add(label);
            if (!string.IsNullOrEmpty(_item.Detail))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = _item.Detail,
                    Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 420,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            return panel;
        }
    }

    public object Description
    {
        get
        {
            var doc = string.IsNullOrWhiteSpace(_item.Documentation) ? _item.Detail : _item.Documentation;
            return new TextBlock { Text = doc, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, FontFamily = new FontFamily("Cascadia Mono,Consolas,DejaVu Sans Mono,monospace"), FontSize = 12 };
        }
    }

    public double Priority { get; }

    public CompletionItem Item => _item;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        var doc = textArea.Document;
        var text = _item.TextToInsert;
        // The segment may be anchor based and move during Replace – remember the start now.
        var start = completionSegment.Offset;
        if (_item.Kind is CompletionKind.Snippet or CompletionKind.Event && text.Contains('\n') || text.Contains("$0"))
        {
            var line = doc.GetLineByOffset(start);
            var lineText = doc.GetText(line.Offset, line.Length);
            var indent = new string(lineText.TakeWhile(char.IsWhiteSpace).ToArray());
            var (expanded, caret) = Snippets.Expand(text, indent, _indentUnit);
            doc.Replace(completionSegment, expanded);
            textArea.Caret.Offset = Math.Min(doc.TextLength, start + caret);
            return;
        }
        doc.Replace(completionSegment, text);
        // Methods: add "()" and place the caret inside when parameters exist – but not when the
        // completion was committed by typing a character such as '(' (it is inserted anyway).
        if (_item.Function != null && _item.Kind is CompletionKind.Method or CompletionKind.Function && insertionRequestEventArgs is not TextInputEventArgs)
        {
            var after = start + text.Length;
            var next = after < doc.TextLength ? doc.GetCharAt(after) : '\0';
            if (next != '(')
            {
                doc.Insert(after, "()");
                textArea.Caret.Offset = after + (_item.Function.Params.Count > 0 ? 1 : 2);
            }
        }
    }

    private static string KindLetter(CompletionKind k) => k switch
    {
        CompletionKind.Keyword => "K",
        CompletionKind.Snippet => "S",
        CompletionKind.Local => "L",
        CompletionKind.Parameter => "P",
        CompletionKind.Global => "G",
        CompletionKind.Function => "F",
        CompletionKind.Method => "M",
        CompletionKind.Field => "•",
        CompletionKind.Library => "B",
        CompletionKind.Class => "C",
        CompletionKind.Event => "E",
        _ => "?",
    };

    private static Color KindColor(CompletionKind k) => k switch
    {
        CompletionKind.Keyword => Color.FromRgb(86, 156, 214),
        CompletionKind.Snippet => Color.FromRgb(120, 120, 120),
        CompletionKind.Local or CompletionKind.Parameter => Color.FromRgb(70, 130, 180),
        CompletionKind.Global => Color.FromRgb(150, 110, 200),
        CompletionKind.Function => Color.FromRgb(180, 140, 40),
        CompletionKind.Method => Color.FromRgb(160, 100, 200),
        CompletionKind.Field => Color.FromRgb(60, 150, 140),
        CompletionKind.Library => Color.FromRgb(40, 140, 120),
        CompletionKind.Event => Color.FromRgb(200, 90, 50),
        _ => Color.FromRgb(100, 100, 100),
    };
}

/// <summary>Signature help ("Parameter-Info") for AvaloniaEdit's OverloadInsightWindow.</summary>
public sealed class SignatureProvider : IOverloadProvider
{
    private SignatureHelp _help;

    public SignatureProvider(SignatureHelp help)
    {
        _help = help;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(SignatureHelp help)
    {
        _help = help;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentHeader)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentContent)));
    }

    public int SelectedIndex { get; set; }
    public int Count => 1;
    public string? CurrentIndexText => null;

    public object CurrentHeader
    {
        get
        {
            var fn = _help.Function;
            var tb = new TextBlock { FontFamily = new FontFamily("Cascadia Mono,Consolas,DejaVu Sans Mono,monospace"), FontSize = 12.5 };
            var inlines = tb.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();
            if (fn.Returns.Count > 0) inlines.Add(new Avalonia.Controls.Documents.Run(string.Join(", ", fn.Returns.Select(r => r.Type)) + " ") { Foreground = new SolidColorBrush(Color.FromRgb(78, 201, 176)) });
            inlines.Add(new Avalonia.Controls.Documents.Run(fn.QualifiedName + "("));
            var active = ActiveIndex(fn);
            for (var i = 0; i < fn.Params.Count; i++)
            {
                if (i > 0) inlines.Add(new Avalonia.Controls.Documents.Run(", "));
                var p = fn.Params[i];
                var run = new Avalonia.Controls.Documents.Run(p.ToString());
                if (i == active)
                {
                    run.FontWeight = FontWeight.Bold;
                    run.Foreground = new SolidColorBrush(Color.FromRgb(255, 215, 0));
                }
                inlines.Add(run);
            }
            inlines.Add(new Avalonia.Controls.Documents.Run(")"));
            return tb;
        }
    }

    private int ActiveIndex(ApiFunction fn)
    {
        var idx = _help.ActiveParameter;
        var vararg = fn.Params.FindIndex(p => p.IsVarArgs);
        if (vararg >= 0 && idx >= vararg) return vararg;
        return idx;
    }

    public object CurrentContent
    {
        get
        {
            var fn = _help.Function;
            var sb = new System.Text.StringBuilder();
            var active = ActiveIndex(fn);
            if (active >= 0 && active < fn.Params.Count)
            {
                var p = fn.Params[active];
                sb.Append(p.IsVarArgs ? "..." : $"{p.Name}: {p.Type}");
                if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append(" – ").Append(ApiText.StripLinks(p.Description));
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(fn.Description))
            {
                var desc = ApiText.StripLinks(fn.Description);
                var para = desc.Split("\n\n")[0];
                sb.AppendLine(para);
            }
            if (_help.Extra != null)
            {
                sb.AppendLine();
                sb.Append("Strg+Leertaste zeigt alle Event-IDs mit Handler-Parametern.");
            }
            return new TextBlock
            {
                Text = sb.ToString().TrimEnd(),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 640,
                FontSize = 12,
            };
        }
    }
}
