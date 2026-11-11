using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Search;
using ElunaWorkbench.Core.Analysis;
using ElunaWorkbench.Core.Api;
using ElunaWorkbench.Core.Lua;
using ElunaWorkbench.Core.Projects;
using ElunaWorkbench.Core.Services;
using ElunaWorkbench.Dialogs;
using ElunaWorkbench.Editor;

namespace ElunaWorkbench;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<ProjectNode> _treeRoots = new();
    private readonly ObservableCollection<ApiNode> _apiRoots = new();
    private readonly DispatcherTimer _apiSearchTimer;
    private readonly DispatcherTimer _problemsTimer;
    private ElunaProject? _project;
    private Workspace _workspace = new();
    private FileSystemWatcher? _watcher;
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        ProjectTree.ItemsSource = _treeRoots;
        ApiTree.ItemsSource = _apiRoots;
        _apiSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _apiSearchTimer.Tick += (_, _) =>
        {
            _apiSearchTimer.Stop();
            RebuildApiTree();
        };
        _problemsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _problemsTimer.Tick += (_, _) =>
        {
            _problemsTimer.Stop();
            RefreshProblems();
        };
        RebuildApiTree();
        UpdateRecentMenus();
        UpdateTitleAndStatus();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Closing += OnWindowClosing;
        UpdatePanelMenuChecks();
    }

    private LuaEditorView? CurrentEditor => (Tabs.SelectedItem as TabItem)?.Content as LuaEditorView;

    private IEnumerable<LuaEditorView> AllEditors => Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<LuaEditorView>();

    public void OpenFromCommandLine(string path)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (path.EndsWith(ElunaProject.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) await OpenProject(path);
            else if (Directory.Exists(path)) await OpenFolderAsProject(path);
            else if (File.Exists(path)) await OpenFile(path);
        });
    }

    // =========================================================================================
    // Keyboard shortcuts
    // =========================================================================================

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.S when ctrl && shift:
                e.Handled = true;
                await SaveAll();
                break;
            case Key.S when ctrl:
                e.Handled = true;
                await SaveCurrent(saveAs: false);
                break;
            case Key.N when ctrl && shift:
                e.Handled = true;
                await NewProject();
                break;
            case Key.N when ctrl:
                e.Handled = true;
                await NewFile();
                break;
            case Key.O when ctrl && shift:
                e.Handled = true;
                await PickAndOpenProject();
                break;
            case Key.O when ctrl:
                e.Handled = true;
                await PickAndOpenFile();
                break;
            case Key.W when ctrl:
                e.Handled = true;
                await CloseTab(Tabs.SelectedItem as TabItem);
                break;
            case Key.G when ctrl:
                e.Handled = true;
                await GoToLine();
                break;
            case Key.H when ctrl:
                e.Handled = true;
                OpenSearch(replace: true);
                break;
            case Key.F6:
                e.Handled = true;
                await Deploy();
                break;
            case Key.F8:
                e.Handled = true;
                NavigateProblem(shift ? -1 : 1);
                break;
            case Key.B when ctrl && shift:
                e.Handled = true;
                await ReanalyzeProject();
                break;
            case Key.F12 when ctrl && shift:
                e.Handled = true;
                await FindInProject();
                break;
            case Key.OemPlus when ctrl:
            case Key.Add when ctrl:
                e.Handled = true;
                Zoom(1);
                break;
            case Key.OemMinus when ctrl:
            case Key.Subtract when ctrl:
                e.Handled = true;
                Zoom(-1);
                break;
            case Key.Tab when ctrl:
                if (Tabs.ItemCount > 1)
                {
                    e.Handled = true;
                    Tabs.SelectedIndex = (Tabs.SelectedIndex + (shift ? Tabs.ItemCount - 1 : 1)) % Tabs.ItemCount;
                }
                break;
        }
    }

    // =========================================================================================
    // Project handling
    // =========================================================================================

    private async Task NewProject()
    {
        var defaultLocation = _settings.LastFolder;
        if (string.IsNullOrWhiteSpace(defaultLocation)) defaultLocation = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(defaultLocation)) defaultLocation = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = await NewProjectDialog.Show(this, defaultLocation);
        if (result == null) return;
        try
        {
            if (Directory.Exists(result.Directory) && Directory.EnumerateFileSystemEntries(result.Directory).Any())
            {
                var answer = await MessageBox.Show(this, "Ordner nicht leer",
                    $"Der Ordner '{result.Directory}' existiert bereits und ist nicht leer. Trotzdem Projekt darin anlegen? (Vorhandene Dateien werden nicht überschrieben.)",
                    DialogResult.Yes, DialogResult.No);
                if (answer != DialogResult.Yes) return;
            }
            var project = ElunaProject.Create(result.Directory, result.Name, result.Template, result.Core, result.LuaVersion);
            _settings.LastFolder = Path.GetDirectoryName(result.Directory);
            await OpenProject(project.FilePath);
            var first = project.EnumerateLuaFiles().FirstOrDefault();
            if (first != null) await OpenFile(first);
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Fehler", "Projekt konnte nicht erstellt werden:\n" + ex.Message);
        }
    }

    private async Task PickAndOpenProject()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Eluna-Projekt öffnen",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Eluna Workbench Projekt") { Patterns = new[] { "*" + ElunaProject.Extension } },
                new FilePickerFileType("Alle Dateien") { Patterns = new[] { "*" } },
            },
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) await OpenProject(path);
    }

    private async Task OpenFolderAsProject(string? folder = null)
    {
        if (folder == null)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Ordner mit Lua-Skripten öffnen (z. B. lua_scripts)" });
            folder = folders.FirstOrDefault()?.TryGetLocalPath();
            if (folder == null) return;
        }
        var existing = Directory.EnumerateFiles(folder, "*" + ElunaProject.Extension).FirstOrDefault();
        if (existing != null)
        {
            await OpenProject(existing);
            return;
        }
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        var project = ElunaProject.Create(folder, string.IsNullOrWhiteSpace(name) ? "Projekt" : name, ProjectTemplate.Empty);
        // Empty template creates "server/" – remove it again if the folder had content already.
        var serverDir = Path.Combine(folder, "server");
        if (Directory.Exists(serverDir) && !Directory.EnumerateFileSystemEntries(serverDir).Any() && Directory.EnumerateFiles(folder, "*.lua", SearchOption.AllDirectories).Any())
        {
            Directory.Delete(serverDir);
        }
        await OpenProject(project.FilePath);
    }

    private async Task OpenProject(string projectFile)
    {
        if (!await CloseProject()) return;
        try
        {
            _project = ElunaProject.Load(projectFile);
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Fehler", "Projekt konnte nicht geladen werden:\n" + ex.Message);
            return;
        }
        _settings.AddRecent(_project.FilePath);
        UpdateRecentMenus();
        _workspace = new Workspace(_project);
        StatusText.Text = "Analysiere Projekt…";
        var ws = _workspace;
        await Task.Run(ws.LoadProjectFiles);
        StatusText.Text = $"Projekt '{_project.Name}' geladen – {ws.Files.Count} Lua-Dateien.";
        RefreshExplorer();
        StartWatcher();
        UpdateTitleAndStatus();
        foreach (var rel in _project.OpenFiles)
        {
            var full = Path.Combine(_project.RootDirectory, rel);
            if (File.Exists(full)) await OpenFile(full);
        }
        ProblemScope.SelectedIndex = 1;
        RefreshProblems();
    }

    private async Task<bool> CloseProject()
    {
        if (!await ConfirmCloseEditors(AllEditors.ToList())) return false;
        if (_project != null)
        {
            _project.OpenFiles = AllEditors.Where(e => e.FilePath != null && e.FilePath.StartsWith(_project.RootDirectory, StringComparison.OrdinalIgnoreCase))
                .Select(e => Path.GetRelativePath(_project.RootDirectory, e.FilePath!)).ToList();
            try { _project.Save(); } catch { /* read-only */ }
        }
        Tabs.Items.Clear();
        _watcher?.Dispose();
        _watcher = null;
        _project = null;
        _workspace = new Workspace();
        _treeRoots.Clear();
        OutlineList.ItemsSource = null;
        UpdateTitleAndStatus();
        RefreshProblems();
        return true;
    }

    private void StartWatcher()
    {
        _watcher?.Dispose();
        if (_project == null) return;
        try
        {
            _watcher = new FileSystemWatcher(_project.RootDirectory) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            FileSystemEventHandler handler = (_, e) => Dispatcher.UIThread.Post(() => OnFileSystemChanged(e.FullPath));
            _watcher.Created += handler;
            _watcher.Deleted += handler;
            _watcher.Renamed += (_, e) => Dispatcher.UIThread.Post(() => OnFileSystemChanged(e.FullPath));
        }
        catch
        {
            _watcher = null;
        }
    }

    private DispatcherTimer? _fsTimer;

    private void OnFileSystemChanged(string path)
    {
        if (_project == null || _project.IsExcluded(path)) return;
        _fsTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _fsTimer.Stop();
        _fsTimer.Tick -= FsTick;
        _fsTimer.Tick += FsTick;
        _fsTimer.Start();
    }

    private void FsTick(object? sender, EventArgs e)
    {
        _fsTimer?.Stop();
        if (_project == null) return;
        // Sync workspace with the files on disk (new / deleted files).
        var onDisk = _project.EnumerateLuaFiles().Select(Workspace.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var openPaths = AllEditors.Select(ed => Workspace.Normalize(ed.AnalysisPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in _workspace.Files)
        {
            if (!onDisk.Contains(f) && !openPaths.Contains(f)) _workspace.RemoveFile(f);
        }
        foreach (var f in onDisk)
        {
            if (_workspace.GetText(f) == null)
            {
                try { _workspace.SetText(f, File.ReadAllText(f)); } catch (IOException) { }
            }
        }
        RefreshExplorer();
        RefreshProblems();
    }

    private void RefreshExplorer()
    {
        _treeRoots.Clear();
        if (_project == null)
        {
            ExplorerHeader.Text = "PROJEKT";
            return;
        }
        ExplorerHeader.Text = _project.Name.ToUpperInvariant();
        var root = ProjectNode.Build(_project.RootDirectory, d => _project.IsExcluded(d), _project.FilePath);
        _treeRoots.Add(root);
        Dispatcher.UIThread.Post(() =>
        {
            if (ProjectTree.ContainerFromIndex(0) is TreeViewItem item) item.IsExpanded = true;
        }, DispatcherPriority.Background);
    }

    // =========================================================================================
    // Files / tabs
    // =========================================================================================

    private async Task NewFile()
    {
        var folder = SelectedFolder();
        if (folder == null)
        {
            // No project: create an untitled editor.
            var template = await NewFileDialog.Show(this, "(ungespeichert)");
            if (template == null) return;
            AddEditor(new LuaEditorView(_workspace, null, template.Value.Template.Content));
            return;
        }
        var result = await NewFileDialog.Show(this, Path.GetRelativePath(_project!.RootDirectory, folder) is "." ? _project.Name : Path.GetRelativePath(_project.RootDirectory, folder));
        if (result == null) return;
        var path = Path.Combine(folder, result.Value.FileName);
        if (File.Exists(path))
        {
            await MessageBox.Show(this, "Datei existiert", $"Die Datei '{result.Value.FileName}' existiert bereits.");
            await OpenFile(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, result.Value.Template.Content);
        _workspace.SetText(path, result.Value.Template.Content);
        RefreshExplorer();
        await OpenFile(path);
    }

    private string? SelectedFolder()
    {
        if (_project == null) return null;
        if (ProjectTree.SelectedItem is ProjectNode node)
        {
            return node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath);
        }
        var server = Path.Combine(_project.RootDirectory, "server");
        return Directory.Exists(server) ? server : _project.RootDirectory;
    }

    private async Task PickAndOpenFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Lua-Datei öffnen",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Lua-Skripte") { Patterns = new[] { "*.lua" } },
                new FilePickerFileType("Eluna Workbench Projekt") { Patterns = new[] { "*" + ElunaProject.Extension } },
                new FilePickerFileType("Alle Dateien") { Patterns = new[] { "*" } },
            },
        });
        foreach (var f in files)
        {
            var path = f.TryGetLocalPath();
            if (path == null) continue;
            if (path.EndsWith(ElunaProject.Extension, StringComparison.OrdinalIgnoreCase)) await OpenProject(path);
            else await OpenFile(path);
        }
    }

    public async Task<LuaEditorView?> OpenFile(string path)
    {
        path = Path.GetFullPath(path);
        foreach (var tab in Tabs.Items.OfType<TabItem>())
        {
            if (tab.Content is LuaEditorView ev && ev.FilePath != null && string.Equals(ev.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                Tabs.SelectedItem = tab;
                ev.FocusEditor();
                return ev;
            }
        }
        if (path.EndsWith(ElunaProject.Extension, StringComparison.OrdinalIgnoreCase) && _project != null)
        {
            await ShowProjectSettings();
            return null;
        }
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Fehler", $"Datei konnte nicht geöffnet werden:\n{ex.Message}");
            return null;
        }
        var editor = new LuaEditorView(_workspace, path, text);
        AddEditor(editor);
        return editor;
    }

    private void AddEditor(LuaEditorView editor)
    {
        editor.Editor.FontSize = _settings.FontSize;
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var close = new Button
        {
            Content = "✕",
            FontSize = 10,
            Padding = new Thickness(4, 0),
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Children = { title, close } };
        var tab = new TabItem { Header = header, Content = editor };
        ToolTip.SetTip(header, editor.FilePath ?? "Ungespeichert");
        close.Click += async (_, _) => await CloseTab(tab);
        header.PointerPressed += async (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsMiddleButtonPressed) await CloseTab(tab);
        };
        void UpdateTitle() => title.Text = editor.Title + (editor.IsDirty ? " ●" : "");
        UpdateTitle();
        editor.DirtyChanged += (_, _) =>
        {
            UpdateTitle();
            ToolTip.SetTip(header, editor.FilePath ?? "Ungespeichert");
        };
        editor.DiagnosticsUpdated += (_, _) =>
        {
            if (editor == CurrentEditor) UpdateOutline();
            _problemsTimer.Stop();
            _problemsTimer.Start();
        };
        editor.CaretMoved += (_, _) =>
        {
            if (editor == CurrentEditor) StatusPosition.Text = $"Z {editor.CaretLine}, Sp {editor.CaretColumn}";
        };
        editor.NavigateRequested += async (_, def) =>
        {
            var target = await OpenFile(def.FilePath);
            target?.GoTo(def.Offset, def.Length);
        };
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        WelcomePanel.IsVisible = false;
        editor.FocusEditor();
    }

    private async Task CloseTab(TabItem? tab)
    {
        if (tab?.Content is not LuaEditorView editor) return;
        if (!await ConfirmCloseEditors(new List<LuaEditorView> { editor })) return;
        Tabs.Items.Remove(tab);
        if (editor.FilePath == null) _workspace.RemoveFile(editor.AnalysisPath);
        else if (_project == null || !editor.FilePath.StartsWith(_project.RootDirectory, StringComparison.OrdinalIgnoreCase)) _workspace.RemoveFile(editor.AnalysisPath);
        else
        {
            // Restore the on-disk version in the workspace (unsaved changes were discarded).
            try { _workspace.SetText(editor.FilePath, await File.ReadAllTextAsync(editor.FilePath)); } catch (IOException) { }
        }
        WelcomePanel.IsVisible = Tabs.ItemCount == 0;
        RefreshProblems();
        UpdateOutline();
    }

    private async Task<bool> ConfirmCloseEditors(List<LuaEditorView> editors)
    {
        foreach (var ed in editors.Where(e => e.IsDirty))
        {
            var answer = await MessageBox.Show(this, "Ungespeicherte Änderungen", $"Änderungen an '{ed.Title}' speichern?", DialogResult.Yes, DialogResult.No, DialogResult.Cancel);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes && !await SaveEditor(ed, saveAs: false)) return false;
        }
        return true;
    }

    private async Task<bool> SaveEditor(LuaEditorView editor, bool saveAs)
    {
        try
        {
            if (editor.FilePath == null || saveAs)
            {
                var start = SelectedFolder() ?? _settings.LastFolder;
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Speichern unter",
                    SuggestedFileName = editor.Title,
                    DefaultExtension = "lua",
                    FileTypeChoices = new[] { new FilePickerFileType("Lua-Skript") { Patterns = new[] { "*.lua" } } },
                    SuggestedStartLocation = start != null ? await StorageProvider.TryGetFolderFromPathAsync(start) : null,
                });
                var path = file?.TryGetLocalPath();
                if (path == null) return false;
                editor.Save(path);
                RefreshExplorer();
            }
            else
            {
                editor.Save();
            }
            StatusText.Text = $"Gespeichert: {editor.Title}";
            return true;
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Fehler beim Speichern", ex.Message);
            return false;
        }
    }

    private async Task SaveCurrent(bool saveAs)
    {
        if (CurrentEditor is { } ed) await SaveEditor(ed, saveAs);
    }

    private async Task SaveAll()
    {
        foreach (var ed in AllEditors.Where(e => e.IsDirty).ToList())
        {
            if (!await SaveEditor(ed, saveAs: false)) return;
        }
        StatusText.Text = "Alle Dateien gespeichert.";
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed) return;
        e.Cancel = true;
        if (await CloseProject())
        {
            _settings.Save();
            _closingConfirmed = true;
            Close();
        }
    }

    // =========================================================================================
    // Tabs, outline, status
    // =========================================================================================

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateOutline();
        RefreshProblems();
        var ed = CurrentEditor;
        StatusPosition.Text = ed != null ? $"Z {ed.CaretLine}, Sp {ed.CaretColumn}" : "";
        UpdateTitleAndStatus();
    }

    private void UpdateOutline()
    {
        var ed = CurrentEditor;
        var model = ed?.Model;
        if (model == null)
        {
            OutlineList.ItemsSource = null;
            return;
        }
        OutlineList.ItemsSource = model.Outline.Select(o =>
        {
            var line = model.Parse.Lines.GetPosition(o.Offset).Line;
            var handler = model.Handlers.FirstOrDefault(h => h.Key.Start == o.Offset).Value;
            return new ListBoxItem
            {
                Content = $"ƒ {o.Name}" + (handler?.Event != null ? $"   ⚡{handler.Event.Name}" : ""),
                Tag = o.Offset,
                Padding = new Thickness(8, 2),
                [ToolTip.TipProperty] = $"Zeile {line}" + (handler != null ? "\n" + handler.Description : ""),
            };
        }).ToList();
    }

    private void UpdateTitleAndStatus()
    {
        var ed = CurrentEditor;
        Title = (ed != null ? ed.Title + " – " : "") + (_project != null ? _project.Name + " – " : "") + "Eluna Workbench";
        StatusCore.Text = "Core: " + (_project?.TargetCore ?? "alle");
        StatusLua.Text = (_project?.LuaVersion ?? LuaVersion.Lua52).DisplayName();
        if (ed?.Model != null) StatusCore.Text += ed.Model.IsClientFile ? "  ·  AIO-Client" : "  ·  Server";
    }

    private void Zoom(int delta)
    {
        _settings.FontSize = Math.Clamp(_settings.FontSize + delta, 8, 32);
        foreach (var ed in AllEditors) ed.Editor.FontSize = _settings.FontSize;
        _settings.Save();
    }

    // =========================================================================================
    // Problems
    // =========================================================================================

    private void RefreshProblems()
    {
        var allFiles = ProblemScope.SelectedIndex == 1;
        var showHints = ShowHints.IsChecked == true;
        List<Diagnostic> diags;
        if (allFiles)
        {
            diags = _workspace.AllDiagnostics();
        }
        else
        {
            diags = CurrentEditor?.Model?.Diagnostics.ToList() ?? new List<Diagnostic>();
        }
        var errors = diags.Count(d => d.Severity == DiagnosticSeverity.Error);
        var warnings = diags.Count(d => d.Severity == DiagnosticSeverity.Warning);
        var infos = diags.Count(d => d.Severity <= DiagnosticSeverity.Info);
        if (!showHints) diags = diags.Where(d => d.Severity > DiagnosticSeverity.Hint).ToList();
        var items = diags
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Start)
            .Select(d => new ListBoxItem
            {
                Content = new ProblemItem { Diagnostic = d }.ToString(),
                Tag = d,
                Padding = new Thickness(8, 2),
                Foreground = new SolidColorBrush(d.Severity switch
                {
                    DiagnosticSeverity.Error => Color.FromRgb(244, 135, 135),
                    DiagnosticSeverity.Warning => Color.FromRgb(230, 200, 110),
                    DiagnosticSeverity.Info => Color.FromRgb(140, 190, 255),
                    _ => Color.FromRgb(160, 160, 160),
                }),
                [ToolTip.TipProperty] = d.Message,
            })
            .ToList();
        ProblemList.ItemsSource = items;
        ProblemsHeader.Text = $"FEHLERLISTE   ⛔ {errors}   ⚠ {warnings}   ℹ {infos}";
        var total = _workspace.AllDiagnostics();
        StatusProblems.Text = $"⛔ {total.Count(d => d.Severity == DiagnosticSeverity.Error)}  ⚠ {total.Count(d => d.Severity == DiagnosticSeverity.Warning)}";
        UpdateTitleAndStatus();
    }

    private async Task NavigateToDiagnostic(Diagnostic d)
    {
        if (d.FilePath == null) return;
        var editor = AllEditors.FirstOrDefault(e => string.Equals(Workspace.Normalize(e.AnalysisPath), Workspace.Normalize(d.FilePath), StringComparison.OrdinalIgnoreCase))
                     ?? await OpenFile(d.FilePath);
        if (editor == null) return;
        Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().First(t => t.Content == editor);
        editor.GoTo(d.Start, d.Length);
    }

    private void NavigateProblem(int direction)
    {
        var ed = CurrentEditor;
        var model = ed?.Model;
        if (ed == null || model == null) return;
        var list = model.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Info).OrderBy(d => d.Start).ToList();
        if (list.Count == 0) return;
        var caret = ed.Editor.CaretOffset;
        var target = direction > 0
            ? list.FirstOrDefault(d => d.Start > caret) ?? list[0]
            : list.LastOrDefault(d => d.Start < caret) ?? list[^1];
        ed.GoTo(target.Start, target.Length);
        StatusText.Text = $"{LuaEditorView.SeverityText(target.Severity)}: {target.Message.Split('\n')[0]}";
    }

    // =========================================================================================
    // API browser
    // =========================================================================================

    private void RebuildApiTree()
    {
        _apiRoots.Clear();
        var filter = ApiSearch.Text ?? "";
        foreach (var n in ApiNode.BuildTree(ApiDatabase.Default, filter)) _apiRoots.Add(n);
        if (filter.Length >= 2)
        {
            Dispatcher.UIThread.Post(() => ExpandAll(ApiTree, 2), DispatcherPriority.Background);
        }
    }

    private static void ExpandAll(ItemsControl control, int depth)
    {
        if (depth <= 0) return;
        for (var i = 0; i < control.ItemCount; i++)
        {
            if (control.ContainerFromIndex(i) is TreeViewItem item && item.ItemCount < 200)
            {
                item.IsExpanded = true;
                Dispatcher.UIThread.Post(() => ExpandAll(item, depth - 1), DispatcherPriority.Background);
            }
        }
    }

    private void InsertIntoEditor(string text)
    {
        var ed = CurrentEditor;
        if (ed == null) return;
        var area = ed.Editor.TextArea;
        var doc = ed.Editor.Document;
        if (text.Contains('\n'))
        {
            var line = doc.GetLineByOffset(ed.Editor.CaretOffset);
            var lineText = doc.GetText(line.Offset, line.Length);
            var indent = new string(lineText.TakeWhile(char.IsWhiteSpace).ToArray());
            var (expanded, caret) = Snippets.Expand(text, indent, LuaEditorView.IndentUnit);
            var start = ed.Editor.CaretOffset;
            doc.Insert(start, expanded);
            area.Caret.Offset = start + caret;
        }
        else
        {
            // ":Method()" only makes sense after an object – strip ':' if not preceded by a name
            var off = ed.Editor.CaretOffset;
            if (text.StartsWith(':') && (off == 0 || !char.IsLetterOrDigit(doc.GetCharAt(off - 1)) && doc.GetCharAt(off - 1) != ')' && doc.GetCharAt(off - 1) != ']'))
            {
                text = text[1..];
            }
            doc.Insert(off, text);
            if (text.EndsWith("()")) area.Caret.Offset = off + text.Length - 1;
        }
        ed.FocusEditor();
    }

    // =========================================================================================
    // Commands
    // =========================================================================================

    private async Task GoToLine()
    {
        var ed = CurrentEditor;
        if (ed == null) return;
        var input = await MessageBox.Prompt(this, "Gehe zu Zeile", $"Zeilennummer (1 – {ed.Editor.Document.LineCount}):", ed.CaretLine.ToString());
        if (int.TryParse(input, out var line)) ed.GoToLine(line);
    }

    private void OpenSearch(bool replace)
    {
        var ed = CurrentEditor;
        if (ed == null) return;
        var searchPanel = ed.Editor.SearchPanel;
        if (searchPanel == null) return;
        searchPanel.IsReplaceMode = replace;
        if (!ed.Editor.TextArea.Selection.IsEmpty && !ed.Editor.TextArea.Selection.IsMultiline)
        {
            searchPanel.SearchPattern = ed.Editor.TextArea.Selection.GetText();
        }
        searchPanel.Open();
        searchPanel.Reactivate();
    }

    private async Task FindInProject()
    {
        var term = await MessageBox.Prompt(this, "In Projekt suchen", "Suchbegriff:", CurrentEditor?.Editor.SelectedText ?? "");
        if (string.IsNullOrEmpty(term)) return;
        var results = new List<(string File, int Line, int Offset, string Text)>();
        foreach (var file in _workspace.Files)
        {
            var text = _workspace.GetText(file) ?? "";
            var lines = new LineIndex(text);
            var idx = 0;
            while ((idx = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var (line, _) = lines.GetPosition(idx);
                var ls = lines.GetLineStart(line);
                var le = text.IndexOf('\n', ls);
                if (le < 0) le = text.Length;
                results.Add((file, line, idx, text.Substring(ls, le - ls).Trim()));
                idx += term.Length;
                if (results.Count > 2000) break;
            }
        }
        var window = MessageBox.CreateWindow($"Suchergebnisse für '{term}' ({results.Count})", 800);
        window.SizeToContent = SizeToContent.Manual;
        window.Height = 480;
        window.CanResize = true;
        var list = new ListBox { FontFamily = new FontFamily("Cascadia Mono,Consolas,DejaVu Sans Mono,monospace"), FontSize = 12 };
        foreach (var r in results)
        {
            list.Items.Add(new ListBoxItem { Content = $"{Path.GetFileName(r.File)}:{r.Line}   {r.Text}", Tag = r });
        }
        list.DoubleTapped += async (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: ValueTuple<string, int, int, string> r })
            {
                var ed = await OpenFile(r.Item1);
                ed?.GoTo(r.Item3, term.Length);
            }
        };
        window.Content = list;
        window.Show(this);
    }

    private async Task Deploy()
    {
        if (_project == null)
        {
            await MessageBox.Show(this, "Deploy", "Bitte zuerst ein Projekt öffnen.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_project.DeployPath))
        {
            await MessageBox.Show(this, "Deploy", "Bitte in den Projekteinstellungen den lua_scripts-Ordner des Servers als Deploy-Pfad festlegen.");
            await ShowProjectSettings();
            if (string.IsNullOrWhiteSpace(_project.DeployPath)) return;
        }
        await SaveAll();
        var errors = _workspace.AllDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            var answer = await MessageBox.Show(this, "Deploy", $"Das Projekt enthält {errors.Count} Fehler. Trotzdem deployen?", DialogResult.Yes, DialogResult.No);
            if (answer != DialogResult.Yes) return;
        }
        try
        {
            var count = _project.Deploy();
            StatusText.Text = $"{count} Dateien nach {_project.DeployPath} kopiert. In-Game mit '.reload eluna' neu laden.";
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Deploy fehlgeschlagen", ex.Message);
        }
    }

    private async Task ShowProjectSettings()
    {
        if (_project == null)
        {
            await MessageBox.Show(this, "Projekteinstellungen", "Kein Projekt geöffnet.");
            return;
        }
        if (await ProjectSettingsDialog.Show(this, _project))
        {
            await ReanalyzeProject();
            RefreshExplorer();
        }
    }

    private async Task ReanalyzeProject()
    {
        StatusText.Text = "Analysiere…";
        var ws = _workspace;
        await Task.Run(() =>
        {
            ws.AnalyzeAll();
            ws.AnalyzeAll();
        });
        foreach (var ed in AllEditors) await ed.AnalyzeAsync();
        RefreshProblems();
        StatusText.Text = $"Analyse abgeschlossen – {ws.Files.Count} Dateien.";
    }

    private void UpdateRecentMenus()
    {
        RecentMenu.Items.Clear();
        var recent = _settings.RecentProjects.Where(File.Exists).ToList();
        foreach (var r in recent)
        {
            var item = new MenuItem { Header = r };
            var path = r;
            item.Click += async (_, _) => await OpenProject(path);
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = recent.Count > 0;

        WelcomeRecent.Items.Clear();
        if (recent.Count > 0) WelcomeRecent.Items.Add(new TextBlock { Text = "Zuletzt geöffnet:", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var r in recent.Take(6))
        {
            var btn = new Button { Content = Path.GetFileNameWithoutExtension(r) + "   —   " + Path.GetDirectoryName(r), Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(55, 148, 255)) };
            var path = r;
            btn.Click += async (_, _) => await OpenProject(path);
            WelcomeRecent.Items.Add(btn);
        }
    }

    private void TogglePanel(Control panel, Control? splitter, int column, GridLength visibleSize)
    {
        panel.IsVisible = !panel.IsVisible;
        if (splitter != null) splitter.IsVisible = panel.IsVisible;
        var grid = RootGrid;
        if (column >= 0)
        {
            grid.ColumnDefinitions[column].Width = panel.IsVisible ? visibleSize : new GridLength(0);
        }
        UpdatePanelMenuChecks();
    }

    private void UpdatePanelMenuChecks()
    {
        ToggleExplorerItem.Icon = ExplorerPanel.IsVisible ? new TextBlock { Text = "✓" } : null;
        ToggleApiItem.Icon = ApiPanel.IsVisible ? new TextBlock { Text = "✓" } : null;
        ToggleProblemsItem.Icon = ProblemsPanel.IsVisible ? new TextBlock { Text = "✓" } : null;
    }

    // =========================================================================================
    // Event handlers (XAML)
    // =========================================================================================

    private async void OnNewProject(object? sender, RoutedEventArgs e) => await NewProject();
    private async void OnOpenProject(object? sender, RoutedEventArgs e) => await PickAndOpenProject();
    private async void OnOpenFolder(object? sender, RoutedEventArgs e) => await OpenFolderAsProject();
    private async void OnNewFile(object? sender, RoutedEventArgs e) => await NewFile();
    private async void OnOpenFile(object? sender, RoutedEventArgs e) => await PickAndOpenFile();
    private async void OnSave(object? sender, RoutedEventArgs e) => await SaveCurrent(saveAs: false);
    private async void OnSaveAs(object? sender, RoutedEventArgs e) => await SaveCurrent(saveAs: true);
    private async void OnSaveAll(object? sender, RoutedEventArgs e) => await SaveAll();
    private async void OnCloseTab(object? sender, RoutedEventArgs e) => await CloseTab(Tabs.SelectedItem as TabItem);
    private async void OnCloseProject(object? sender, RoutedEventArgs e) => await CloseProject();
    private void OnExit(object? sender, RoutedEventArgs e) => Close();
    private void OnUndo(object? sender, RoutedEventArgs e) => CurrentEditor?.Editor.Undo();
    private void OnRedo(object? sender, RoutedEventArgs e) => CurrentEditor?.Editor.Redo();
    private void OnFind(object? sender, RoutedEventArgs e) => OpenSearch(replace: false);
    private void OnReplace(object? sender, RoutedEventArgs e) => OpenSearch(replace: true);
    private async void OnFindInProject(object? sender, RoutedEventArgs e) => await FindInProject();
    private async void OnGoToLine(object? sender, RoutedEventArgs e) => await GoToLine();
    private void OnGoToDefinition(object? sender, RoutedEventArgs e) => CurrentEditor?.GoToDefinition();
    private void OnComplete(object? sender, RoutedEventArgs e) => CurrentEditor?.ShowCompletion(true);
    private void OnParameterInfo(object? sender, RoutedEventArgs e) => CurrentEditor?.ShowSignatureHelp(true);
    private void OnToggleComment(object? sender, RoutedEventArgs e) => CurrentEditor?.ToggleComment();
    private void OnFormat(object? sender, RoutedEventArgs e) => CurrentEditor?.FormatDocument();
    private void OnQuickFix(object? sender, RoutedEventArgs e) => CurrentEditor?.ShowQuickFixes();

    private void OnFixAll(object? sender, RoutedEventArgs e)
    {
        var ed = CurrentEditor;
        if (ed == null) return;
        var n = ed.FixAll();
        StatusText.Text = n == 0 ? "Keine automatisch behebbaren Probleme gefunden." : $"{n} Korrektur(en) angewendet.";
    }

    private void OnNextProblem(object? sender, RoutedEventArgs e) => NavigateProblem(1);
    private void OnPreviousProblem(object? sender, RoutedEventArgs e) => NavigateProblem(-1);
    private async void OnProjectSettings(object? sender, RoutedEventArgs e) => await ShowProjectSettings();
    private async void OnReanalyze(object? sender, RoutedEventArgs e) => await ReanalyzeProject();
    private async void OnDeploy(object? sender, RoutedEventArgs e) => await Deploy();
    private void OnToggleExplorer(object? sender, RoutedEventArgs e) => TogglePanel(ExplorerPanel, LeftSplitter, 0, new GridLength(260));
    private void OnToggleApi(object? sender, RoutedEventArgs e) => TogglePanel(ApiPanel, RightSplitter, 4, new GridLength(320));

    private void OnToggleProblems(object? sender, RoutedEventArgs e)
    {
        ProblemsPanel.IsVisible = !ProblemsPanel.IsVisible;
        BottomSplitter.IsVisible = ProblemsPanel.IsVisible;
        RootGrid.RowDefinitions[2].Height = ProblemsPanel.IsVisible ? new GridLength(190) : new GridLength(0);
        UpdatePanelMenuChecks();
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Zoom(1);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => Zoom(-1);

    private async void OnShortcuts(object? sender, RoutedEventArgs e)
    {
        await MessageBox.Show(this, "Tastenkürzel", """
            Strg+Leertaste           Autovervollständigung
            Strg+Umschalt+Leertaste  Parameter-Info
            Strg+.                   Schnellkorrektur (Korrekturvorschläge)
            F8 / Umschalt+F8         Nächstes / vorheriges Problem
            F12                      Gehe zu Definition
            Strg+K                   Kommentar umschalten
            Strg+Umschalt+F          Dokument formatieren
            Strg+F / Strg+H          Suchen / Ersetzen
            Strg+Umschalt+F12        In Projekt suchen
            Strg+G                   Gehe zu Zeile
            Strg+S / Strg+Umschalt+S Speichern / Alle speichern
            Strg+N                   Neue Datei
            Strg+W                   Datei schließen
            Strg+Tab                 Nächster Tab
            Strg+Umschalt+B          Projekt neu analysieren
            F6                       Deploy nach lua_scripts

            Tipp: Tippen Sie den Namen eines Events (z. B. PLAYER_EVENT_ON_LOGIN),
            um einen fertigen Handler inkl. Registrierung einzufügen.
            """);
    }

    private async void OnAbout(object? sender, RoutedEventArgs e)
    {
        var api = ApiDatabase.Default;
        await MessageBox.Show(this, "Über Eluna Workbench",
            $"Eluna Workbench 1.0\n\nEditor für die Eluna Lua Engine (https://github.com/ElunaLuaEngine/Eluna) und AIO (https://github.com/Rochet2/AIO).\n\n" +
            $"API-Datenbank: {api.Classes.Count(c => c.Source == ApiSource.Eluna)} Klassen, {api.Classes.Sum(c => c.Methods.Count)} Methoden, " +
            $"{api.Globals.Count(g => g.Source == ApiSource.Eluna)} globale Funktionen, {api.AllEvents.Count()} Events.\n" +
            $"Cores: {string.Join(", ", api.Cores)}");
    }

    private async void OnProjectTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ProjectTree.SelectedItem is ProjectNode { IsDirectory: false } node) await OpenFile(node.FullPath);
    }

    private async void OnTreeOpen(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is ProjectNode { IsDirectory: false } node) await OpenFile(node.FullPath);
    }

    private async void OnNewFolder(object? sender, RoutedEventArgs e)
    {
        var parent = SelectedFolder();
        if (parent == null) return;
        var name = await MessageBox.Prompt(this, "Neuer Ordner", "Ordnername:", "neuer_ordner");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(parent, ElunaProject.SanitizeFileName(name)));
            RefreshExplorer();
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Fehler", ex.Message);
        }
    }

    private async void OnTreeRename(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectNode node || node == _treeRoots.FirstOrDefault()) return;
        var newName = await MessageBox.Prompt(this, "Umbenennen", "Neuer Name:", node.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == node.Name) return;
        var target = Path.Combine(Path.GetDirectoryName(node.FullPath)!, newName);
        try
        {
            if (node.IsDirectory)
            {
                Directory.Move(node.FullPath, target);
                foreach (var ed in AllEditors.Where(ed => ed.FilePath != null && ed.FilePath.StartsWith(node.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    var newPath = Path.Combine(target, Path.GetRelativePath(node.FullPath, ed.FilePath!));
                    _workspace.RenameFile(ed.FilePath!, newPath);
                    ed.Rename(newPath);
                }
            }
            else
            {
                File.Move(node.FullPath, target);
                _workspace.RenameFile(node.FullPath, target);
                foreach (var ed in AllEditors.Where(ed => string.Equals(ed.FilePath, node.FullPath, StringComparison.OrdinalIgnoreCase))) ed.Rename(target);
            }
            RefreshExplorer();
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Fehler", ex.Message);
        }
    }

    private async void OnTreeDelete(object? sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectNode node || node == _treeRoots.FirstOrDefault() || node.IsProjectFile) return;
        var answer = await MessageBox.Show(this, "Löschen", $"'{node.Name}' wirklich löschen?{(node.IsDirectory ? " Der gesamte Ordnerinhalt wird gelöscht." : "")}", DialogResult.Yes, DialogResult.No);
        if (answer != DialogResult.Yes) return;
        try
        {
            var affected = AllEditors.Where(ed => ed.FilePath != null &&
                (string.Equals(ed.FilePath, node.FullPath, StringComparison.OrdinalIgnoreCase) || ed.FilePath.StartsWith(node.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var tab in Tabs.Items.OfType<TabItem>().Where(t => affected.Contains(t.Content as LuaEditorView ?? null!)).ToList()) Tabs.Items.Remove(tab);
            if (node.IsDirectory) Directory.Delete(node.FullPath, recursive: true);
            else File.Delete(node.FullPath);
            foreach (var f in _workspace.Files.Where(f => f.StartsWith(node.FullPath, StringComparison.OrdinalIgnoreCase))) _workspace.RemoveFile(f);
            RefreshExplorer();
            RefreshProblems();
            WelcomePanel.IsVisible = Tabs.ItemCount == 0;
        }
        catch (Exception ex)
        {
            await MessageBox.Show(this, "Fehler", ex.Message);
        }
    }

    private void OnRefreshExplorer(object? sender, RoutedEventArgs e) => RefreshExplorer();

    private void OnOutlineDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (OutlineList.SelectedItem is ListBoxItem { Tag: int offset }) CurrentEditor?.GoTo(offset);
    }

    private async void OnProblemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ProblemList.SelectedItem is ListBoxItem { Tag: Diagnostic d }) await NavigateToDiagnostic(d);
    }

    private void OnProblemFilterChanged(object? sender, RoutedEventArgs e)
    {
        if (ProblemList != null) RefreshProblems();
    }

    private void OnProblemContextOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ProblemContextMenu.Items.Clear();
        if (ProblemList.SelectedItem is not ListBoxItem { Tag: Diagnostic d })
        {
            e.Cancel = true;
            return;
        }
        var go = new MenuItem { Header = "Gehe zu" };
        go.Click += async (_, _) => await NavigateToDiagnostic(d);
        ProblemContextMenu.Items.Add(go);
        foreach (var fix in d.Fixes)
        {
            var item = new MenuItem { Header = "💡 " + fix.Title };
            item.Click += async (_, _) =>
            {
                await NavigateToDiagnostic(d);
                CurrentEditor?.ApplyFix(fix);
            };
            ProblemContextMenu.Items.Add(item);
        }
        var copy = new MenuItem { Header = "Meldung kopieren" };
        copy.Click += async (_, _) =>
        {
            if (Clipboard != null) await Clipboard.SetTextAsync($"{d.FilePath}:{d.Line}:{d.Column}: {d.Code}: {d.Message}");
        };
        ProblemContextMenu.Items.Add(copy);
    }

    private void OnApiSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _apiSearchTimer.Stop();
        _apiSearchTimer.Start();
    }

    private void OnApiSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApiDoc.Text = (ApiTree.SelectedItem as ApiNode)?.Documentation ?? "";
    }

    private void OnApiDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ApiTree.SelectedItem is ApiNode { InsertText: { } text }) InsertIntoEditor(text);
    }
}
