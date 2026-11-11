using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Indentation;
using AvaloniaEdit.Search;
using ElunaWorkbench.Core.Analysis;
using ElunaWorkbench.Core.Lua;
using ElunaWorkbench.Core.Projects;
using ElunaWorkbench.Core.Services;

namespace ElunaWorkbench.Editor;

/// <summary>
/// The Lua/Eluna code editor: syntax highlighting, live diagnostics with squiggles, quick fixes,
/// auto completion, signature help, hover documentation, folding, go-to-definition,
/// reference highlighting, auto indentation and auto "end".
/// </summary>
public sealed class LuaEditorView : UserControl
{
    public const string IndentUnit = "    ";

    private static int _untitledCounter;

    private readonly Workspace _workspace;
    private readonly TextEditor _editor;
    private readonly DiagnosticRenderer _renderer;
    private readonly FoldingManager _folding;
    private readonly DispatcherTimer _analysisTimer;
    private readonly DispatcherTimer _caretTimer;
    private CompletionWindow? _completionWindow;
    private OverloadInsightWindow? _insightWindow;
    private SignatureProvider? _signatureProvider;
    private int _changeCounter;
    private bool _dirty;
    private string _savedText;

    public LuaEditorView(Workspace workspace, string? filePath, string text)
    {
        _workspace = workspace;
        FilePath = filePath;
        AnalysisPath = filePath ?? Path.Combine(Path.GetTempPath(), $"untitled-{Interlocked.Increment(ref _untitledCounter)}.lua");
        _savedText = text;

        _editor = new TextEditor
        {
            FontFamily = new FontFamily("Cascadia Mono,Consolas,JetBrains Mono,DejaVu Sans Mono,monospace"),
            FontSize = 14,
            ShowLineNumbers = true,
            SyntaxHighlighting = LuaHighlighting.Definition,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 212)),
            LineNumbersForeground = new SolidColorBrush(Color.FromRgb(110, 118, 129)),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(4, 0, 0, 0),
        };
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 4;
        _editor.Options.HighlightCurrentLine = true;
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.ShowBoxForControlCharacters = true;
        _editor.Options.CutCopyWholeLine = true;
        _editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        _editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent);
        _editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(110, 38, 79, 120));
        _editor.Document = new TextDocument(text);
        _editor.TextArea.IndentationStrategy = new LuaIndentationStrategy();

        _renderer = new DiagnosticRenderer(_editor.Document);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        _folding = FoldingManager.Install(_editor.TextArea);
        SearchPanel.Install(_editor);

        _analysisTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _analysisTimer.Tick += (_, _) =>
        {
            _analysisTimer.Stop();
            _ = AnalyzeAsync();
        };
        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _caretTimer.Tick += (_, _) =>
        {
            _caretTimer.Stop();
            UpdateReferenceHighlights();
        };

        _editor.Document.TextChanged += OnTextChanged;
        _editor.TextArea.TextEntering += OnTextEntering;
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            CaretMoved?.Invoke(this, EventArgs.Empty);
            _caretTimer.Stop();
            _caretTimer.Start();
            UpdateSignatureHelpIfOpen();
        };
        _editor.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _editor.PointerHover += OnPointerHover;
        _editor.PointerHoverStopped += (_, _) => ToolTip.SetIsOpen(_editor, false);
        _editor.ContextMenu = new ContextMenu();
        _editor.ContextMenu.Opening += (_, _) => BuildContextMenu(_editor.ContextMenu);
        ToolTip.SetShowDelay(_editor, 400);

        Content = _editor;
        _ = AnalyzeAsync();
    }

    public string? FilePath { get; private set; }

    /// <summary>Path used in the workspace (a temp name for unsaved files).</summary>
    public string AnalysisPath { get; private set; }

    public TextEditor Editor => _editor;

    public SemanticModel? Model { get; private set; }

    public string Title => FilePath != null ? Path.GetFileName(FilePath) : Path.GetFileName(AnalysisPath);

    public bool IsDirty
    {
        get => _dirty;
        private set
        {
            if (_dirty == value) return;
            _dirty = value;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? DirtyChanged;
    public event EventHandler? DiagnosticsUpdated;
    public event EventHandler? CaretMoved;
    public event EventHandler<DefinitionLocation>? NavigateRequested;

    public int CaretLine => _editor.TextArea.Caret.Line;
    public int CaretColumn => _editor.TextArea.Caret.Column;

    public string Text => _editor.Document.Text;

    // -----------------------------------------------------------------------------------------
    // File handling
    // -----------------------------------------------------------------------------------------

    public void Save(string? newPath = null)
    {
        if (newPath != null)
        {
            var old = AnalysisPath;
            FilePath = newPath;
            AnalysisPath = newPath;
            _workspace.RemoveFile(old);
        }
        if (FilePath == null) throw new InvalidOperationException("Kein Dateipfad.");
        File.WriteAllText(FilePath, _editor.Document.Text);
        _savedText = _editor.Document.Text;
        IsDirty = false;
        _ = AnalyzeAsync();
    }

    public void ReloadFromDisk()
    {
        if (FilePath == null || !File.Exists(FilePath)) return;
        var text = File.ReadAllText(FilePath);
        _editor.Document.Text = text;
        _savedText = text;
        IsDirty = false;
    }

    public void Rename(string newPath)
    {
        FilePath = newPath;
        AnalysisPath = newPath;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    public void FocusEditor()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _editor.Focus();
            _editor.TextArea.Focus();
        });
    }

    public void GoTo(int offset, int length = 0)
    {
        offset = Math.Clamp(offset, 0, _editor.Document.TextLength);
        _editor.TextArea.Caret.Offset = offset;
        if (length > 0) _editor.Select(offset, Math.Min(length, _editor.Document.TextLength - offset));
        var loc = _editor.Document.GetLocation(offset);
        _editor.ScrollTo(loc.Line, loc.Column);
        FocusEditor();
    }

    public void GoToLine(int line)
    {
        line = Math.Clamp(line, 1, _editor.Document.LineCount);
        GoTo(_editor.Document.GetLineByNumber(line).Offset);
    }

    // -----------------------------------------------------------------------------------------
    // Analysis
    // -----------------------------------------------------------------------------------------

    private void OnTextChanged(object? sender, EventArgs e)
    {
        _changeCounter++;
        IsDirty = _editor.Document.TextLength != _savedText.Length || _editor.Document.Text != _savedText;
        _analysisTimer.Stop();
        _analysisTimer.Start();
    }

    public async Task AnalyzeAsync()
    {
        var version = _changeCounter;
        var text = _editor.Document.Text;
        var path = AnalysisPath;
        SemanticModel model;
        try
        {
            model = await Task.Run(() =>
            {
                _workspace.SetText(path, text, analyze: false);
                return _workspace.Analyze(path);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Analysis failed: " + ex);
            return;
        }
        if (version != _changeCounter) return; // outdated
        Model = model;
        _renderer.SetDiagnostics(model.Diagnostics);
        UpdateFoldings(model);
        _editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
        DiagnosticsUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Analyses the current text synchronously (used for completion).</summary>
    private SemanticModel CurrentModel() => _workspace.AnalyzeText(AnalysisPath, _editor.Document.Text);

    private void UpdateFoldings(SemanticModel model)
    {
        var doc = _editor.Document;
        var foldings = new List<NewFolding>();
        foreach (var (start, end) in model.Parse.BlockRanges)
        {
            if (end > doc.TextLength || start >= end) continue;
            var startLine = doc.GetLineByOffset(start);
            var endLine = doc.GetLineByOffset(end);
            if (startLine.LineNumber == endLine.LineNumber) continue;
            var f = new NewFolding(startLine.EndOffset, end) { Name = "..." };
            foldings.Add(f);
        }
        // Multi-line comments
        foreach (var t in model.Parse.Tokens)
        {
            if (t.Kind != TokenKind.Comment || !t.Text.StartsWith("--[") || t.End > doc.TextLength) continue;
            var sl = doc.GetLineByOffset(t.Start);
            var el = doc.GetLineByOffset(t.End);
            if (sl.LineNumber != el.LineNumber) foldings.Add(new NewFolding(t.Start, t.End) { Name = "--[[ ... ]]" });
        }
        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        _folding.UpdateFoldings(foldings, -1);
    }

    private void UpdateReferenceHighlights()
    {
        var model = Model;
        if (model == null || model.Text.Length != _editor.Document.TextLength)
        {
            _renderer.SetHighlights(new());
            return;
        }
        var refs = LanguageService.GetLocalReferences(model, _editor.CaretOffset);
        _renderer.SetHighlights(refs.Count > 1 ? refs : new());
        _editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
    }

    // -----------------------------------------------------------------------------------------
    // Typing: completion, signature help, auto indent, auto end
    // -----------------------------------------------------------------------------------------

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || _completionWindow == null) return;
        var c = e.Text[0];
        if (char.IsLetterOrDigit(c) || c == '_') return;
        // Commit characters: only commit "simple" items (names, methods) that match what was typed.
        if (c is '(' or '.' or ':' or ',' && _completionWindow.CompletionList.SelectedItem is CompletionData { Item: var item })
        {
            var typed = _editor.Document.GetText(_completionWindow.StartOffset, Math.Max(0, _editor.CaretOffset - _completionWindow.StartOffset));
            var simple = item.Kind is not (CompletionKind.Snippet or CompletionKind.Event or CompletionKind.Keyword) && !(item.InsertText?.Contains('\n') ?? false);
            if (simple && typed.Length > 0 && item.Label.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            {
                _completionWindow.CompletionList.RequestInsertion(e);
                return;
            }
            if (item.Kind == CompletionKind.Event && item.InsertText is { } ins && !ins.Contains('\n') && typed.Length > 0 && item.Label.StartsWith(typed))
            {
                _completionWindow.CompletionList.RequestInsertion(e);
                return;
            }
        }
        _completionWindow.Close();
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        var c = e.Text[^1];
        switch (c)
        {
            case '.':
            case ':':
                if (!IsAfterDoubleDot()) ShowCompletion(explicitInvoke: false);
                break;
            case '(':
            case ',':
                ShowSignatureHelp();
                if (c == ',' || c == '(') ShowArgumentCompletionIfUseful();
                break;
            case ')':
                _insightWindow?.Close();
                break;
            case '\n':
            case '\r':
                AutoInsertEnd();
                break;
            default:
                if (char.IsLetter(c) || c == '_')
                {
                    ReindentClosingKeyword();
                    if (_completionWindow == null && WordBeforeCaretLength() == 1) ShowCompletion(explicitInvoke: false);
                }
                break;
        }
    }

    private bool IsAfterDoubleDot()
    {
        var off = _editor.CaretOffset;
        return off >= 2 && _editor.Document.GetCharAt(off - 2) == '.';
    }

    private int WordBeforeCaretLength()
    {
        var doc = _editor.Document;
        var off = _editor.CaretOffset;
        var i = off;
        while (i > 0 && (char.IsLetterOrDigit(doc.GetCharAt(i - 1)) || doc.GetCharAt(i - 1) == '_')) i--;
        if (i < off && char.IsDigit(doc.GetCharAt(i))) return 0;
        return off - i;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && e.Key == Key.Space)
        {
            if (shift) ShowSignatureHelp(force: true);
            else ShowCompletion(explicitInvoke: true);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.OemPeriod)
        {
            ShowQuickFixes();
            e.Handled = true;
        }
        else if (e.Key == Key.F12)
        {
            GoToDefinition();
            e.Handled = true;
        }
        else if (ctrl && (e.Key == Key.K || e.Key == Key.OemQuestion || e.Key == Key.Divide))
        {
            ToggleComment();
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.F)
        {
            FormatDocument();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _insightWindow?.Close();
        }
    }

    public void ShowCompletion(bool explicitInvoke)
    {
        if (_completionWindow != null) return;
        var model = CurrentModel();
        var result = LanguageService.GetCompletions(model, _editor.CaretOffset, explicitInvoke);
        if (result.Items.Count == 0) return;
        if (!explicitInvoke && !result.IsMemberCompletion && result.Prefix.Length == 0) return;

        var window = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = result.ReplaceStart,
            EndOffset = result.ReplaceStart + result.ReplaceLength,
            CloseWhenCaretAtBeginning = !result.IsMemberCompletion,
            CloseAutomatically = true,
            MinWidth = 420,
        };
        window.CompletionList.IsFiltering = true;
        var data = window.CompletionList.CompletionData;
        var n = result.Items.Count;
        for (var i = 0; i < n; i++)
        {
            data.Add(new CompletionData(result.Items[i], n - i, IndentUnit));
        }
        _completionWindow = window;
        window.Closed += (_, _) => _completionWindow = null;
        window.Show();
        if (result.Prefix.Length > 0) window.CompletionList.SelectItem(result.Prefix);
        else window.CompletionList.SelectedItem = data.FirstOrDefault();
    }

    private void ShowArgumentCompletionIfUseful()
    {
        var model = CurrentModel();
        var call = LanguageService.FindEnclosingCall(model, _editor.CaretOffset);
        if (call == null) return;
        var fn = LanguageService.ResolveCall(model, call.Value, _editor.CaretOffset);
        if (fn == null || fn.Owner != null || model.Api.FindHook(fn.Name) is not { } hook) return;
        if (call.Value.ArgumentIndex != hook.EventArgIndex && call.Value.ArgumentIndex != hook.HandlerArgIndex) return;
        Dispatcher.UIThread.Post(() => ShowCompletion(explicitInvoke: true), DispatcherPriority.Background);
    }

    public void ShowSignatureHelp(bool force = false)
    {
        var model = CurrentModel();
        var help = LanguageService.GetSignatureHelp(model, _editor.CaretOffset);
        if (help == null)
        {
            _insightWindow?.Close();
            return;
        }
        if (_insightWindow != null && _signatureProvider != null)
        {
            _signatureProvider.Update(help);
            return;
        }
        _signatureProvider = new SignatureProvider(help);
        _insightWindow = new OverloadInsightWindow(_editor.TextArea) { Provider = _signatureProvider, MaxWidth = 700 };
        _insightWindow.Closed += (_, _) =>
        {
            _insightWindow = null;
            _signatureProvider = null;
        };
        _insightWindow.Show();
    }

    private void UpdateSignatureHelpIfOpen()
    {
        if (_insightWindow == null) return;
        var model = CurrentModel();
        var help = LanguageService.GetSignatureHelp(model, _editor.CaretOffset);
        if (help == null) _insightWindow.Close();
        else _signatureProvider?.Update(help);
    }

    /// <summary>After Enter behind "then"/"do"/"function(...)": insert the matching "end" if it is missing.</summary>
    private void AutoInsertEnd()
    {
        var doc = _editor.Document;
        var caretLine = doc.GetLineByOffset(_editor.CaretOffset);
        var prev = caretLine.PreviousLine;
        if (prev == null) return;
        var prevText = doc.GetText(prev.Offset, prev.Length);
        var trimmed = StripTrailingComment(prevText).TrimEnd();
        var opensBlock = trimmed.EndsWith(" then") || trimmed == "then" || trimmed.EndsWith(" do") || trimmed == "do" || trimmed == "else"
                         || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"\bfunction\b[^()]*\([^()]*\)$") || trimmed.EndsWith("repeat");
        if (!opensBlock || trimmed == "else") return;
        var parse = Parser.Parse(doc.Text);
        if (!parse.Diagnostics.Any(d => d.Code == "LUA102")) return;
        var indent = new string(prevText.TakeWhile(char.IsWhiteSpace).ToArray());
        var closer = trimmed.EndsWith("repeat") ? "until " : "end";
        // Inline functions passed as argument: close the call as well when "(" is unbalanced on the line.
        var opens = trimmed.Count(ch => ch == '(') - trimmed.Count(ch => ch == ')');
        if (opens > 0 && System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"\bfunction\b")) closer += new string(')', opens);
        var caret = _editor.CaretOffset;
        var restOfLine = doc.GetText(caret, caretLine.EndOffset - caret);
        doc.Insert(caretLine.EndOffset, "\n" + indent + closer);
        if (restOfLine.Trim().Length == 0) _editor.CaretOffset = caret;
    }

    private static string StripTrailingComment(string line)
    {
        var inString = '\0';
        for (var i = 0; i < line.Length - 1; i++)
        {
            var c = line[i];
            if (inString != '\0')
            {
                if (c == '\\') i++;
                else if (c == inString) inString = '\0';
                continue;
            }
            if (c is '"' or '\'') inString = c;
            else if (c == '-' && line[i + 1] == '-') return line[..i];
        }
        return line;
    }

    /// <summary>Typing "end", "else", "elseif", "until" at the start of a line fixes its indentation.</summary>
    private void ReindentClosingKeyword()
    {
        var doc = _editor.Document;
        var line = doc.GetLineByOffset(_editor.CaretOffset);
        var text = doc.GetText(line.Offset, line.Length);
        var trimmed = text.Trim();
        if (trimmed is not ("end" or "else" or "elseif" or "until")) return;
        if (_editor.CaretOffset != line.EndOffset) return;
        var before = doc.GetText(0, line.Offset);
        var level = Math.Max(0, Formatter.IndentLevelAfter(before) - 1);
        var newIndent = string.Concat(Enumerable.Repeat(IndentUnit, level));
        var oldIndentLength = text.Length - text.TrimStart().Length;
        if (text[..oldIndentLength] == newIndent) return;
        doc.Replace(line.Offset, oldIndentLength, newIndent);
    }

    // -----------------------------------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------------------------------

    public void FormatDocument()
    {
        var text = _editor.Document.Text;
        var formatted = Formatter.Format(text, IndentUnit);
        if (formatted == text) return;
        var caret = _editor.CaretOffset;
        var line = _editor.TextArea.Caret.Line;
        _editor.Document.BeginUpdate();
        _editor.Document.Replace(0, _editor.Document.TextLength, formatted);
        _editor.Document.EndUpdate();
        var l = Math.Min(line, _editor.Document.LineCount);
        _editor.TextArea.Caret.Offset = Math.Min(_editor.Document.GetLineByNumber(l).EndOffset, Math.Max(caret, _editor.Document.GetLineByNumber(l).Offset));
    }

    public void ToggleComment()
    {
        var doc = _editor.Document;
        var area = _editor.TextArea;
        var startLine = doc.GetLineByOffset(area.Selection.IsEmpty ? _editor.CaretOffset : area.Selection.SurroundingSegment.Offset);
        var endLine = doc.GetLineByOffset(area.Selection.IsEmpty ? _editor.CaretOffset : area.Selection.SurroundingSegment.EndOffset);
        var lines = new List<DocumentLine>();
        for (var l = startLine; l != null && l.LineNumber <= endLine.LineNumber; l = l.NextLine) lines.Add(l);
        var allCommented = lines.Where(l => doc.GetText(l.Offset, l.Length).Trim().Length > 0)
            .All(l => doc.GetText(l.Offset, l.Length).TrimStart().StartsWith("--"));
        doc.BeginUpdate();
        foreach (var l in lines.AsEnumerable().Reverse())
        {
            var text = doc.GetText(l.Offset, l.Length);
            var indent = text.Length - text.TrimStart().Length;
            if (allCommented)
            {
                var idx = text.IndexOf("--", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var len = idx + 2 < text.Length && text[idx + 2] == ' ' ? 3 : 2;
                    doc.Remove(l.Offset + idx, len);
                }
            }
            else if (text.Trim().Length > 0)
            {
                doc.Insert(l.Offset + indent, "-- ");
            }
        }
        doc.EndUpdate();
    }

    public void GoToDefinition()
    {
        var model = CurrentModel();
        var def = LanguageService.GetDefinition(model, _editor.CaretOffset);
        if (def == null) return;
        if (string.Equals(Workspace.Normalize(def.FilePath), Workspace.Normalize(AnalysisPath), StringComparison.OrdinalIgnoreCase))
        {
            GoTo(def.Offset, def.Length);
        }
        else
        {
            NavigateRequested?.Invoke(this, def);
        }
    }

    public IReadOnlyList<Diagnostic> DiagnosticsAt(int offset)
    {
        var model = Model;
        if (model == null) return Array.Empty<Diagnostic>();
        return model.Diagnostics.Where(d => offset >= d.Start && offset <= Math.Max(d.End, d.Start + 1)).ToList();
    }

    public void ApplyFix(CodeFix fix)
    {
        var doc = _editor.Document;
        doc.BeginUpdate();
        foreach (var edit in fix.Edits.OrderByDescending(e => e.Start))
        {
            var start = Math.Clamp(edit.Start, 0, doc.TextLength);
            var len = Math.Clamp(edit.Length, 0, doc.TextLength - start);
            doc.Replace(start, len, edit.NewText);
        }
        doc.EndUpdate();
        _ = AnalyzeAsync();
    }

    /// <summary>Applies the first fix of every diagnostic of the given codes (or of all fixable ones).</summary>
    public int FixAll(Func<Diagnostic, bool>? filter = null)
    {
        var applied = 0;
        for (var round = 0; round < 10; round++)
        {
            var model = CurrentModel();
            var d = model.Diagnostics.FirstOrDefault(x => x.Fixes.Count > 0 && x.Severity >= DiagnosticSeverity.Warning && (filter == null || filter(x))
                                                          && x.Code is not ("ELU004" or "ELU007" or "AIO003"));
            if (d == null) break;
            ApplyFix(d.Fixes[0]);
            applied++;
        }
        return applied;
    }

    public void ShowQuickFixes()
    {
        var model = Model;
        if (model == null) return;
        var diags = DiagnosticsAt(_editor.CaretOffset).Where(d => d.Fixes.Count > 0).ToList();
        var menu = new ContextMenu();
        if (diags.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Keine Korrekturen an dieser Stelle", IsEnabled = false });
        }
        foreach (var d in diags)
        {
            foreach (var fix in d.Fixes)
            {
                var item = new MenuItem { Header = "💡 " + fix.Title };
                item.Click += (_, _) => ApplyFix(fix);
                menu.Items.Add(item);
            }
        }
        var caretRect = _editor.TextArea.Caret.CalculateCaretRectangle();
        var textView = _editor.TextArea.TextView;
        var p = textView.TranslatePoint(new Point(caretRect.X - textView.HorizontalOffset, caretRect.Bottom - textView.VerticalOffset), _editor) ?? new Point();
        menu.Placement = PlacementMode.AnchorAndGravity;
        menu.PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
        menu.PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
        menu.PlacementRect = new Rect(p, new Size(1, 1));
        menu.Open(_editor);
        if (menu.Items.Count > 0 && menu.Items[0] is MenuItem first) first.Focus();
    }

    private void BuildContextMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var offset = _editor.CaretOffset;
        var fixes = DiagnosticsAt(offset).SelectMany(d => d.Fixes).ToList();
        foreach (var fix in fixes)
        {
            var item = new MenuItem { Header = "💡 " + fix.Title };
            item.Click += (_, _) => ApplyFix(fix);
            menu.Items.Add(item);
        }
        if (fixes.Count > 0) menu.Items.Add(new Separator());
        AddMenu(menu, "Gehe zu Definition", "F12", GoToDefinition);
        AddMenu(menu, "Autovervollständigung", "Strg+Leertaste", () => ShowCompletion(true));
        AddMenu(menu, "Parameter-Info", "Strg+Umschalt+Leertaste", () => ShowSignatureHelp(true));
        menu.Items.Add(new Separator());
        AddMenu(menu, "Ausschneiden", "Strg+X", () => _editor.Cut());
        AddMenu(menu, "Kopieren", "Strg+C", () => _editor.Copy());
        AddMenu(menu, "Einfügen", "Strg+V", () => _editor.Paste());
        menu.Items.Add(new Separator());
        AddMenu(menu, "Kommentar umschalten", "Strg+K", ToggleComment);
        AddMenu(menu, "Dokument formatieren", "Strg+Umschalt+F", FormatDocument);
        AddMenu(menu, "Alle sicheren Korrekturen anwenden", "", () => FixAll());
    }

    private static void AddMenu(ContextMenu menu, string header, string gesture, Action action)
    {
        var item = new MenuItem { Header = header, InputGesture = string.IsNullOrEmpty(gesture) ? null : TryGesture(gesture) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static KeyGesture? TryGesture(string text)
    {
        try
        {
            return KeyGesture.Parse(text.Replace("Strg", "Ctrl").Replace("Umschalt", "Shift").Replace("Leertaste", "Space"));
        }
        catch
        {
            return null;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Hover
    // -----------------------------------------------------------------------------------------

    private void OnPointerHover(object? sender, PointerEventArgs e)
    {
        var pos = _editor.GetPositionFromPoint(e.GetPosition(_editor));
        if (pos == null)
        {
            ToolTip.SetIsOpen(_editor, false);
            return;
        }
        var offset = _editor.Document.GetOffset(pos.Value.Location);
        var model = Model;
        if (model == null || model.Text.Length != _editor.Document.TextLength) return;

        var panel = new StackPanel { Spacing = 4, MaxWidth = 640 };
        foreach (var d in DiagnosticsAt(offset))
        {
            var color = d.Severity switch
            {
                DiagnosticSeverity.Error => Color.FromRgb(244, 100, 100),
                DiagnosticSeverity.Warning => Color.FromRgb(230, 190, 60),
                DiagnosticSeverity.Info => Color.FromRgb(100, 170, 255),
                _ => Color.FromRgb(170, 170, 170),
            };
            panel.Children.Add(new TextBlock
            {
                Text = $"{SeverityText(d.Severity)} {d.Code}: {d.Message}" + (d.Fixes.Count > 0 ? "\n💡 Strg+. für Korrekturen" : ""),
                Foreground = new SolidColorBrush(color),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        var hover = LanguageService.GetHover(model, offset);
        if (hover != null)
        {
            if (panel.Children.Count > 0) panel.Children.Add(new Separator { Margin = new Thickness(0, 2) });
            panel.Children.Add(new TextBlock
            {
                Text = hover.Title,
                FontFamily = new FontFamily("Cascadia Mono,Consolas,DejaVu Sans Mono,monospace"),
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrWhiteSpace(hover.Body))
            {
                panel.Children.Add(new TextBlock { Text = hover.Body, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
            }
        }
        if (panel.Children.Count == 0)
        {
            ToolTip.SetIsOpen(_editor, false);
            return;
        }
        ToolTip.SetTip(_editor, panel);
        ToolTip.SetIsOpen(_editor, true);
        e.Handled = true;
    }

    public static string SeverityText(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Error => "Fehler",
        DiagnosticSeverity.Warning => "Warnung",
        DiagnosticSeverity.Info => "Info",
        _ => "Hinweis",
    };
}

/// <summary>Indents new lines according to the Lua block structure.</summary>
public sealed class LuaIndentationStrategy : IIndentationStrategy
{
    public void IndentLine(TextDocument document, DocumentLine line)
    {
        var prev = line.PreviousLine;
        if (prev == null) return;
        var before = document.GetText(0, line.Offset);
        var level = Formatter.IndentLevelAfter(before);
        var current = document.GetText(line.Offset, line.Length);
        var trimmed = current.TrimStart();
        if (trimmed.StartsWith("end") || trimmed.StartsWith("else") || trimmed.StartsWith("until") || trimmed.StartsWith("}")) level--;
        var indent = string.Concat(Enumerable.Repeat(LuaEditorView.IndentUnit, Math.Max(0, level)));
        var oldIndent = current.Length - trimmed.Length;
        document.Replace(line.Offset, oldIndent, indent);
    }

    public void IndentLines(TextDocument document, int beginLine, int endLine)
    {
        for (var i = beginLine; i <= endLine; i++) IndentLine(document, document.GetLineByNumber(i));
    }
}
