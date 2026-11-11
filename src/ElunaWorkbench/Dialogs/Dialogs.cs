using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ElunaWorkbench.Core.Analysis;
using ElunaWorkbench.Core.Api;
using ElunaWorkbench.Core.Projects;

namespace ElunaWorkbench.Dialogs;

public enum DialogResult
{
    None,
    Yes,
    No,
    Cancel,
    Ok,
}

/// <summary>Small helper dialogs (message box, input box) built in code.</summary>
public static class MessageBox
{
    public static async Task<DialogResult> Show(Window owner, string title, string message, params DialogResult[] buttons)
    {
        if (buttons.Length == 0) buttons = new[] { DialogResult.Ok };
        var result = DialogResult.Cancel;
        var dialog = CreateWindow(title, 460);
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons)
        {
            var btn = new Button { Content = Label(b), MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            var value = b;
            btn.Click += (_, _) =>
            {
                result = value;
                dialog.Close();
            };
            if (b is DialogResult.Ok or DialogResult.Yes) btn.IsDefault = true;
            if (b is DialogResult.Cancel) btn.IsCancel = true;
            buttonPanel.Children.Add(btn);
        }
        panel.Children.Add(buttonPanel);
        dialog.Content = panel;
        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task<string?> Prompt(Window owner, string title, string label, string initial = "")
    {
        string? result = null;
        var dialog = CreateWindow(title, 420);
        var box = new TextBox { Text = initial, MinWidth = 360 };
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Abbrechen", MinWidth = 90, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) =>
        {
            result = box.Text;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = label },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            var dot = initial.LastIndexOf('.');
            box.SelectionStart = 0;
            box.SelectionEnd = dot > 0 ? dot : initial.Length;
        };
        await dialog.ShowDialog(owner);
        return result;
    }

    internal static Window CreateWindow(string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
        Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
    };

    private static string Label(DialogResult r) => r switch
    {
        DialogResult.Yes => "Ja",
        DialogResult.No => "Nein",
        DialogResult.Cancel => "Abbrechen",
        _ => "OK",
    };
}

public sealed class NewProjectResult
{
    public required string Name { get; init; }
    public required string Directory { get; init; }
    public required ProjectTemplate Template { get; init; }
    public string? Core { get; init; }
    public LuaVersion LuaVersion { get; init; }
}

public static class NewProjectDialog
{
    public static async Task<NewProjectResult?> Show(Window owner, string defaultLocation)
    {
        NewProjectResult? result = null;
        var dialog = MessageBox.CreateWindow("Neues Eluna-Projekt", 560);
        var name = new TextBox { Text = "MeinElunaProjekt" };
        var location = new TextBox { Text = defaultLocation };
        var browse = new Button { Content = "…" };
        browse.Click += async (_, _) =>
        {
            var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Speicherort wählen", AllowMultiple = false });
            var f = folders.FirstOrDefault()?.TryGetLocalPath();
            if (f != null) location.Text = f;
        };
        var templates = new ListBox { Height = 200 };
        foreach (var (t, n, d) in ProjectTemplates.All)
        {
            templates.Items.Add(new ListBoxItem
            {
                Tag = t,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = n, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = d, FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap },
                    },
                },
            });
        }
        templates.SelectedIndex = 1;
        var core = CoreBox(ApiDatabase.Default, "TrinityCore");
        var lua = LuaVersionBox(LuaVersion.Lua52);
        var preview = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        void UpdatePreview() => preview.Text = "Projektordner: " + Path.Combine(location.Text ?? "", ElunaProject.SanitizeFileName(name.Text ?? ""));
        name.TextChanged += (_, _) => UpdatePreview();
        location.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        var ok = new Button { Content = "Erstellen", IsDefault = true, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(location.Text)) return;
            result = new NewProjectResult
            {
                Name = name.Text.Trim(),
                Directory = Path.Combine(location.Text.Trim(), ElunaProject.SanitizeFileName(name.Text.Trim())),
                Template = (templates.SelectedItem as ListBoxItem)?.Tag is ProjectTemplate t ? t : ProjectTemplate.Empty,
                Core = (core.SelectedItem as ComboBoxItem)?.Tag as string,
                LuaVersion = (lua.SelectedItem as ComboBoxItem)?.Tag is LuaVersion v ? v : LuaVersion.Lua52,
            };
            dialog.Close();
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto") };
        AddRow(grid, 0, "Name:", name);
        AddRow(grid, 1, "Speicherort:", location, browse);
        AddRow(grid, 2, "Emulator-Core:", core);
        AddRow(grid, 3, "Lua-Version:", lua);
        AddRow(grid, 4, "Vorlage:", templates);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                grid,
                preview,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        await dialog.ShowDialog(owner);
        return result;
    }

    internal static void AddRow(Grid grid, int row, string label, Control control, Control? extra = null)
    {
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
        control.Margin = new Thickness(0, 4);
        if (extra != null) extra.Margin = new Thickness(6, 4, 0, 4);
        Grid.SetRow(l, row);
        grid.Children.Add(l);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        if (extra == null) Grid.SetColumnSpan(control, 2);
        grid.Children.Add(control);
        if (extra != null)
        {
            Grid.SetRow(extra, row);
            Grid.SetColumn(extra, 2);
            grid.Children.Add(extra);
        }
    }

    internal static ComboBox CoreBox(ApiDatabase api, string? selected)
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        box.Items.Add(new ComboBoxItem { Content = "Alle Cores (keine Prüfung)", Tag = null });
        foreach (var c in api.Cores) box.Items.Add(new ComboBoxItem { Content = c, Tag = c });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected)) ?? box.Items[0];
        return box;
    }

    internal static ComboBox LuaVersionBox(LuaVersion selected)
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var v in Enum.GetValues<LuaVersion>()) box.Items.Add(new ComboBoxItem { Content = v.DisplayName(), Tag = v });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, selected));
        return box;
    }
}

public static class ProjectSettingsDialog
{
    public static async Task<bool> Show(Window owner, ElunaProject project)
    {
        var saved = false;
        var dialog = MessageBox.CreateWindow("Projekteinstellungen – " + project.Name, 600);
        var name = new TextBox { Text = project.Name };
        var core = NewProjectDialog.CoreBox(ApiDatabase.Default, project.TargetCore);
        var lua = NewProjectDialog.LuaVersionBox(project.LuaVersion);
        var deploy = new TextBox { Text = project.DeployPath ?? "", Watermark = "…/server/lua_scripts" };
        var browse = new Button { Content = "…" };
        browse.Click += async (_, _) =>
        {
            var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "lua_scripts-Ordner des Servers wählen" });
            var f = folders.FirstOrDefault()?.TryGetLocalPath();
            if (f != null) deploy.Text = f;
        };
        var client = new TextBox { Text = string.Join(", ", project.ClientFolders) };
        var excluded = new TextBox { Text = string.Join(", ", project.ExcludedFolders) };
        var o = project.Diagnostics;
        var undefinedGlobals = new CheckBox { Content = "Unbekannte globale Variablen melden", IsChecked = o.ReportUndefinedGlobals };
        var unused = new CheckBox { Content = "Unbenutzte lokale Variablen melden", IsChecked = o.ReportUnusedLocals };
        var globalAssign = new CheckBox { Content = "Globale Zuweisungen in Funktionen melden (fehlendes 'local')", IsChecked = o.ReportGlobalAssignmentInFunction };
        var argCount = new CheckBox { Content = "Argumentanzahl von Eluna-Funktionen prüfen", IsChecked = o.ReportArgumentCount };
        var coreCheck = new CheckBox { Content = "Verfügbarkeit von Methoden im gewählten Core prüfen", IsChecked = o.ReportCoreCompatibility };
        var nameTypes = new CheckBox { Content = "Typen aus Parameternamen ableiten (player → Player, creature → Creature, …)", IsChecked = o.InferTypesFromParameterNames };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto") };
        NewProjectDialog.AddRow(grid, 0, "Name:", name);
        NewProjectDialog.AddRow(grid, 1, "Emulator-Core:", core);
        NewProjectDialog.AddRow(grid, 2, "Lua-Version:", lua);
        NewProjectDialog.AddRow(grid, 3, "Deploy-Pfad:", deploy, browse);
        NewProjectDialog.AddRow(grid, 4, "Client-Ordner (AIO):", client);
        NewProjectDialog.AddRow(grid, 5, "Ausgeschlossen:", excluded);

        var ok = new Button { Content = "Speichern", IsDefault = true, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) =>
        {
            project.Name = string.IsNullOrWhiteSpace(name.Text) ? project.Name : name.Text.Trim();
            project.TargetCore = (core.SelectedItem as ComboBoxItem)?.Tag as string;
            project.LuaVersion = (lua.SelectedItem as ComboBoxItem)?.Tag is LuaVersion v ? v : project.LuaVersion;
            project.DeployPath = string.IsNullOrWhiteSpace(deploy.Text) ? null : deploy.Text.Trim();
            project.ClientFolders = Split(client.Text);
            project.ExcludedFolders = Split(excluded.Text);
            o.ReportUndefinedGlobals = undefinedGlobals.IsChecked == true;
            o.ReportUnusedLocals = unused.IsChecked == true;
            o.ReportGlobalAssignmentInFunction = globalAssign.IsChecked == true;
            o.ReportArgumentCount = argCount.IsChecked == true;
            o.ReportCoreCompatibility = coreCheck.IsChecked == true;
            o.InferTypesFromParameterNames = nameTypes.IsChecked == true;
            project.Save();
            saved = true;
            dialog.Close();
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                grid,
                new TextBlock { Text = "Diagnosen", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                undefinedGlobals, unused, globalAssign, argCount, coreCheck, nameTypes,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        await dialog.ShowDialog(owner);
        return saved;
    }

    private static List<string> Split(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

public static class NewFileDialog
{
    public static async Task<(string FileName, FileTemplate Template)?> Show(Window owner, string folderDisplay)
    {
        (string, FileTemplate)? result = null;
        var dialog = MessageBox.CreateWindow("Neue Lua-Datei", 520);
        var name = new TextBox { Text = "script.lua" };
        var list = new ListBox { Height = 220 };
        foreach (var t in FileTemplates.All)
        {
            list.Items.Add(new ListBoxItem
            {
                Tag = t,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = t.Name, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = t.Description, FontSize = 11, Foreground = Brushes.Gray },
                    },
                },
            });
        }
        list.SelectedIndex = 0;
        var userEdited = false;
        name.TextChanged += (_, _) => userEdited = name.IsFocused || userEdited;
        list.SelectionChanged += (_, _) =>
        {
            if (!userEdited && list.SelectedItem is ListBoxItem { Tag: FileTemplate t }) name.Text = t.DefaultFileName;
        };
        var ok = new Button { Content = "Erstellen", IsDefault = true, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => dialog.Close();
        ok.Click += (_, _) =>
        {
            var n = (name.Text ?? "").Trim();
            if (n.Length == 0) return;
            if (!n.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) n += ".lua";
            result = (n, (list.SelectedItem as ListBoxItem)?.Tag as FileTemplate ?? FileTemplates.Empty);
            dialog.Close();
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Ordner: " + folderDisplay, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Vorlage:" },
                list,
                new TextBlock { Text = "Dateiname:" },
                name,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        await dialog.ShowDialog(owner);
        return result;
    }
}
