using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using D2RModelLodDescEditor.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace D2RModelLodDescEditor.ViewModels;

/// <summary>
/// Three-pane editor over <see cref="LodDescDocument"/>: models[] on the left,
/// the selected asset's ModelInfo slots + meshes in the middle, and the record
/// fields of the current selection on the right. Field edits patch the live
/// buffer in place (write-through); structural edits repack on save.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private Window? _window;
    private LodDescDocument? _doc;

    /// <summary>Slot LOD to re-select after a repack rebuilt the record graph.</summary>
    private int _restoreLod = -1;

    public ObservableCollection<ModelAssetRecord> Models { get; } = new();
    public ObservableCollection<ModelInfoRecord> Slots { get; } = new();
    public ObservableCollection<MeshRecord> Meshes { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ModelAssetRecord? _selectedModel;

    [ObservableProperty]
    private ModelInfoRecord? _selectedSlot;

    [ObservableProperty]
    private MeshRecord? _selectedMesh;

    [ObservableProperty]
    private string _statusText = "Open model_lod_desc.json (the paired .bin loads with it) to begin.";

    [ObservableProperty]
    private string _title = "model_lod_desc editor";

    // ------------------------------------------------------------- selection

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedModelChanged(ModelAssetRecord? value)
    {
        FillSlots();
        RaiseState();
    }

    partial void OnSelectedSlotChanged(ModelInfoRecord? value)
    {
        FillMeshes();
        RaiseState();
    }

    partial void OnSelectedMeshChanged(MeshRecord? value) => RaiseState();

    public bool HasDocument => _doc is not null;
    public bool HasModel => SelectedModel is not null;
    public bool HasSlot => SelectedSlot is not null;
    public bool HasMesh => SelectedSlot is not null && SelectedMesh is not null;

    private void RaiseState()
    {
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(HasSlot));
        OnPropertyChanged(nameof(HasMesh));
        RefreshTitle();
        AddMeshCommand.NotifyCanExecuteChanged();
        RemoveMeshCommand.NotifyCanExecuteChanged();
        SortByHashCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        RecomputeBoundsCommand.NotifyCanExecuteChanged();
        IntegrityCommand.NotifyCanExecuteChanged();
        AddModelCommand.NotifyCanExecuteChanged();
        RemoveModelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Selects a record by hash, bypassing the active filter.</summary>
    private void SelectModel(ModelAssetRecord m)
    {
        SearchText = string.Empty;
        ApplyFilter();
        SelectedModel = Models.Contains(m) ? m : Models.FirstOrDefault(x => x.Hash == m.Hash);
    }

    private void FillSlots()
    {
        Slots.Clear();
        if (SelectedModel is not null)
            foreach (var s in SelectedModel.Slots) Slots.Add(s);
        int want = _restoreLod;
        _restoreLod = -1;
        SelectedSlot = want >= 0 && Slots.Any(s => s.SlotLod == want)
            ? Slots.First(s => s.SlotLod == want)
            : Slots.Count > 0 ? Slots[0] : null;
        FillMeshes();
    }

    private void FillMeshes()
    {
        Meshes.Clear();
        if (SelectedSlot is not null)
            foreach (var m in SelectedSlot.Meshes) Meshes.Add(m);
        SelectedMesh = Meshes.Count > 0 ? Meshes[0] : null;
    }

    private void ApplyFilter()
    {
        if (_doc is null) return;
        string q = SearchText.Trim();
        IEnumerable<ModelAssetRecord> src = _doc.Models;
        if (q.Length > 0)
        {
            if (q.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                string body = q[2..];
                if (uint.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint full))
                    src = src.Where(m => m.Hash == full || m.HashHex.Contains(body, StringComparison.OrdinalIgnoreCase));
                else
                    src = src.Where(m => m.Display.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                src = src.Where(m => m.Display.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
        }

        ModelAssetRecord? keep = SelectedModel;
        Models.Clear();
        foreach (var m in src) Models.Add(m);
        if (keep is not null && !Models.Contains(keep)) SelectedModel = null;
    }

    private void RefreshTitle()
    {
        Title = _doc is null
            ? "model_lod_desc editor"
            : $"{(_doc.FilePath is null ? "untitled" : Path.GetFileName(_doc.FilePath))}" +
              $"{(_doc.ManifestLoaded ? " [+manifest]" : "")}" +
              $"{(_doc.IsDirty || _doc.IsManifestDirty ? " *" : "")} — {_doc.Summary}";
    }

    // -------------------------------------------------------------- document

    /// <summary>Wires this view model to the window that owns the dialogs.</summary>
    public void Attach(Window window) => _window = window;

    private void ReplaceDocument(LodDescDocument doc)
    {
        if (_doc is not null) _doc.StructureChanged -= OnStructureChanged;
        _doc = doc;
        doc.StructureChanged += OnStructureChanged;

        uint? keepHash = SelectedModel?.Hash;
        _restoreLod = SelectedSlot?.SlotLod ?? -1;

        SearchText = string.Empty;
        ApplyFilter();   // SearchText may already be empty (no change event) — rebuild explicitly
        SelectedModel = keepHash is uint h
            ? Models.FirstOrDefault(m => m.Hash == h) ?? (Models.Count > 0 ? Models[0] : null)
            : Models.Count > 0 ? Models[0] : null;
        RaiseState();
    }

    /// <summary>Loads a parsed document into the view (public for the self-test).</summary>
    public void LoadDocument(LodDescDocument doc)
    {
        ReplaceDocument(doc);
        StatusText = $"Loaded {Path.GetFileName(doc.FilePath ?? "model_lod_desc.bin")} — {doc.Summary}";
    }

    private void OnStructureChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnStructureChanged);
            return;
        }
        if (_doc is null) return;
        uint? keepHash = SelectedModel?.Hash;
        _restoreLod = SelectedSlot?.SlotLod ?? -1;
        SearchText = string.Empty;
        ApplyFilter();   // SearchText may already be empty (no change event) — rebuild explicitly
        SelectedModel = keepHash is uint h
            ? Models.FirstOrDefault(m => m.Hash == h) ?? (Models.Count > 0 ? Models[0] : null)
            : Models.Count > 0 ? Models[0] : null;
        RaiseState();
    }

    // --------------------------------------------------------------- commands

    [RelayCommand]
    private async Task OpenBinAsync()
    {
        var files = await PickAsync("Open model_lod_desc.bin", "model_lod_desc.bin", "*.bin");
        if (files.Count == 0) return;
        string path = files[0].Path.LocalPath;
        try
        {
            var doc = LodDescDocument.Load(path);
            LoadDocument(doc);
            // pair discovery: a sibling manifest (same stem) indexes the hashes
            // automatically, so add/remove can mirror into it on save.
            string json = LodDescDocument.ManifestPathFor(path);
            if (File.Exists(json))
            {
                doc.LoadPaths(json);
                StatusText = $"Loaded {Path.GetFileName(path)} + manifest ({doc.PathCount} keys) — {doc.Summary}";
            }
            else
            {
                StatusText = $"Loaded {Path.GetFileName(path)} — {doc.Summary} " +
                             "(no manifest paired: add/remove won't touch a .json)";
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Cannot open .bin", ex);
        }
    }

    [RelayCommand]
    private async Task OpenJsonAsync()
    {
        // Manifest-first open: the .bin the json is paired with loads with it.
        // Without a paired bin the editor refuses (both halves are needed).
        var files = await PickAsync("Open model_lod_desc.json (loads the paired .bin)",
            "model_lod_desc.json", "*.json");
        if (files.Count == 0) return;
        string json = files[0].Path.LocalPath;
        try
        {
            var doc = LodDescDocument.LoadPair(json);
            LoadDocument(doc);
            StatusText = $"Loaded manifest {Path.GetFileName(json)} + bin " +
                         $"({doc.PathCount} keys) — {doc.Summary}";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Cannot open manifest", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task SaveAsync()
    {
        if (_doc is null) return;
        if (_doc.FilePath is null) { await SaveAsAsync(); return; }
        SaveTo(_doc.FilePath);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (_doc is null || _window is null) return;
        var file = await _window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save model_lod_desc.bin",
            SuggestedFileName = Path.GetFileName(_doc.FilePath ?? "model_lod_desc.bin"),
            DefaultExtension = "bin",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("model_lod_desc.bin") { Patterns = new[] { "*.bin" } },
            },
        });
        if (file is null) return;
        SaveTo(file.Path.LocalPath);
    }

    /// <summary>Writes only the manifest of the loaded pair (next to the .bin).</summary>
    [RelayCommand]
    private void SaveManifest()
    {
        if (_doc is null) return;
        if (!_doc.ManifestLoaded)
        {
            StatusText = "No manifest paired — nothing to save.";
            return;
        }
        string json = LodDescDocument.ManifestPathFor(_doc.FilePath ?? "model_lod_desc.bin");
        _doc.SaveManifest(json);
        StatusText = $"Manifest saved: {json}";
        RefreshTitle();
    }

    private void SaveTo(string path)
    {
        // Save() repacks when structural edits happened, re-parses, and raises
        // StructureChanged — the selection is restored by hash/LOD there.
        _doc!.Save(path);
        // The manifest is the other half of the pair: it rides along next to
        // the .bin (same stem) and already reflects add/remove/lodMask edits.
        if (_doc.ManifestLoaded)
        {
            string json = LodDescDocument.ManifestPathFor(path);
            _doc.SaveManifest(json);
            StatusText = $"Saved {path} + manifest {Path.GetFileName(json)} — {_doc.Summary}";
        }
        else
        {
            StatusText = $"Saved {path} — {_doc.Summary} (no manifest paired)";
        }
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void SortByHash()
    {
        _doc!.SortByHash();
        StatusText = "models[] re-sorted ascending by hash (structure dirty — save to persist)";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedModel))]
    private void RecomputeBounds()
    {
        _doc!.RecomputeBounds(SelectedModel!);
        RefreshTitle();
        StatusText = $"AABB recomputed for {SelectedModel!.Display} (union of its mesh boxes)";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSlot))]
    private void AddMesh()
    {
        var slot = SelectedSlot!;
        var mesh = _doc!.AddMesh(slot);
        FillMeshes();
        SelectedMesh = mesh;
        StatusText = $"Pending mesh appended to {slot.Display} — materializes on save";
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSlotAndMesh))]
    private void RemoveMesh()
    {
        var slot = SelectedSlot!;
        var mesh = SelectedMesh!;
        _doc!.RemoveMesh(slot, mesh);
        FillMeshes();
        StatusText = $"Mesh removed from {slot.Display} — materializes on save";
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task AddModelAsync()
    {
        if (_doc is null) return;
        var spec = await ShowAddModelDialogAsync();
        if (spec is null) return;
        try
        {
            ModelAssetRecord added;
            if (spec.Files.Count > 0)
            {
                // Fields filled from the parsed .model file(s) — the verified
                // offline-writer recipe (Granny ExtendedData -> bin records).
                var im = ModelImporter.ParseFiles(spec.Files, spec.Key);
                added = _doc.AddImportedModel(im);
                StatusText = $"Added {spec.Key} — {im.Slots.Count} slot(s), {im.MeshCount} mesh(es) " +
                             "from the model file (pending until save)";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(spec.Key))
                {
                    StatusText = "Add model cancelled: a manifest key is required.";
                    return;
                }
                uint hash = LodDescDocument.FnvHash(spec.Key.Trim().ToLowerInvariant());
                added = _doc.AddModel(hash, spec.LodMask, spec.Key.Trim());
                for (int lod = 0; lod < 8; lod++)
                    if (added.GetLodBit(lod))
                        _doc.AddSlot(added, lod, spec.MeshesPerSlot);
                StatusText = $"Added {spec.Key} ({spec.MeshesPerSlot} mesh(es)/slot) — pending until save";
            }
            if (_doc.ManifestLoaded)
                StatusText += " — manifest entry added";
            SelectModel(added);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Add model failed", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedModel))]
    private void RemoveModel()
    {
        var m = SelectedModel!;
        _doc!.RemoveModel(m);
        SelectedModel = Models.Count > 0 ? Models[0] : null;
        StatusText = $"Removed {m.Display} — materializes on save" +
                     (_doc.ManifestLoaded ? " — manifest entry removed" : "");
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Integrity()
    {
        var issues = _doc!.Integrity();
        var sb = new StringBuilder();
        sb.AppendLine(_doc.Summary);
        sb.AppendLine(_doc.HasPaths
            ? $"manifest: {_doc.PathCount} keys, {_doc.PathCollisions} hash collisions"
            : "manifest: not loaded");
        sb.AppendLine();
        if (issues.Count == 0) sb.AppendLine("clean — no issues");
        else foreach (var i in issues) sb.AppendLine(i);
        ShowReport("Integrity check", sb.ToString());
        StatusText = issues.Count == 0 ? "Integrity: clean" : $"Integrity: {issues.Count} issue(s)";
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void Exit() => _window?.Close();

    // --------------------------------------------------------------- theme
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThemeDefault), nameof(IsThemeLight), nameof(IsThemeDark))]
    private string _currentTheme = "Default";

    public bool IsThemeDefault => _currentTheme == "Default";
    public bool IsThemeLight => _currentTheme == "Light";
    public bool IsThemeDark => _currentTheme == "Dark";

    [RelayCommand]
    private void SetTheme(string? variant)
    {
        var v = variant?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        if (Application.Current is { } app)
            app.RequestedThemeVariant = v;
        CurrentTheme = v == ThemeVariant.Light ? "Light" : v == ThemeVariant.Dark ? "Dark" : "Default";
    }

    // ---------------------------------------------------------------- helpers

    private bool HasSelectedModel => SelectedModel is not null;
    private bool HasSelectedSlot => SelectedSlot is not null;
    private bool HasSelectedSlotAndMesh => SelectedSlot is not null && SelectedMesh is not null;

    private async Task<IReadOnlyList<IStorageFile>> PickAsync(string title, string suggested, params string[] patterns)
    {
        if (_window is null) return Array.Empty<IStorageFile>();
        var opts = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new FilePickerFileType(suggested) { Patterns = patterns },
            },
        };
        return await _window.StorageProvider.OpenFilePickerAsync(opts);
    }

    private async Task<IReadOnlyList<IStorageFile>> PickManyAsync(string title, string suggested, params string[] patterns)
    {
        if (_window is null) return Array.Empty<IStorageFile>();
        var opts = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new FilePickerFileType(suggested) { Patterns = patterns },
            },
        };
        return await _window.StorageProvider.OpenFilePickerAsync(opts);
    }

    // ------------------------------------------------------- add-model dialog

    /// <summary>What the add-model dialog collects.</summary>
    private sealed record AddModelSpec(string Key, byte LodMask, int MeshesPerSlot,
                                       List<string> Files);

    /// <summary>
    /// Unified add-model dialog: type/confirm the manifest key, optionally
    /// pick .model file(s) to fill every field from the file's granny
    /// ExtendedData, or fall back to a blank skeleton (lodMask + meshes/slot).
    /// </summary>
    private async Task<AddModelSpec?> ShowAddModelDialogAsync()
    {
        if (_window is null) return null;

        var keyBox = new TextBox { PlaceholderText = "data/hd/.../name.model" };
        var hashView = new TextBlock { Opacity = 0.7, Margin = new Thickness(0, 0, 0, 8) };
        var picked = new List<string>();
        var fileSummary = new TextBlock
        {
            Text = "No .model file chosen — a blank skeleton will be created.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8),
        };
        var lodChecks = Enumerable.Range(0, 8)
            .Select(i => new CheckBox { Content = $"L{i}", Margin = new Thickness(0, 0, 8, 0) })
            .ToArray();
        var meshPerSlot = new TextBox { Text = "0", Width = 70 };

        void RefreshHash()
        {
            string k = keyBox.Text?.Trim() ?? "";
            hashView.Text = k.Length == 0
                ? "hash: —"
                : $"hash: 0x{LodDescDocument.FnvHash(k.ToLowerInvariant()):X8}";
        }
        keyBox.TextChanged += (_, _) => RefreshHash();
        RefreshHash();

        var pickButton = new Button { Content = "Choose .model file(s)…" };
        pickButton.Click += async (_, _) =>
        {
            try
            {
                var files = await PickManyAsync("Choose the model file(s) to import", "model file", "*.model");
                if (files.Count == 0) return;
                picked.Clear();
                picked.AddRange(files.Select(f => f.Path.LocalPath));
                if (string.IsNullOrWhiteSpace(keyBox.Text))
                    keyBox.Text = ModelImporter.GuessKey(picked[0]);
                var im = ModelImporter.ParseFiles(picked, keyBox.Text?.Trim() ?? ModelImporter.GuessKey(picked[0]));
                for (int i = 0; i < 8; i++) lodChecks[i].IsChecked = im.Slots.Any(s => s.Lod == i);
                fileSummary.Text = $"{im.SourceFiles.Count} file(s): " +
                    string.Join("; ", im.Slots.Select(s => $"LOD {s.Lod}: {s.Meshes.Count} mesh(es)")) +
                    $"; typeMask 0x{im.TypeMask:X8}{(im.IsFlipbook ? "; flipbook" : "")}";
                RefreshHash();
            }
            catch (Exception ex)
            {
                fileSummary.Text = "import failed: " + ex.Message;
            }
        };

        var addButton = new Button { Content = "Add", IsDefault = true, Classes = { "accent" } };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true };
        var tcs = new TaskCompletionSource<AddModelSpec?>();

        var lodRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        foreach (var c in lodChecks) lodRow.Children.Add(c);

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Manifest key (the hash is FNV-1a32 of the lowercased key):" });
        panel.Children.Add(keyBox);
        panel.Children.Add(hashView);
        panel.Children.Add(pickButton);
        panel.Children.Add(fileSummary);
        panel.Children.Add(new TextBlock { Text = "LOD slots (derived from the file when imported):", Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(lodRow);
        panel.Children.Add(new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 12),
            Children =
            {
                new TextBlock { Text = "Meshes per new slot (blank skeleton):", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                meshPerSlot,
            },
        });
        panel.Children.Add(new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Children = { addButton, cancelButton },
        });

        var win = new Window
        {
            Title = "Add model",
            Width = 520,
            SizeToContent = Avalonia.Controls.SizeToContent.WidthAndHeight,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            Content = panel,
        };

        // Dialog buttons must BOTH resolve the task and dismiss the window:
        // setting the TCS alone leaves the dialog on screen (Avalonia's
        // Button.IsCancel wires the Escape key but does not close a dialog
        // window on click, and the Add button never closed it either).
        addButton.Click += (_, _) =>
        {
            byte mask = 0;
            for (int i = 0; i < 8; i++) if (lodChecks[i].IsChecked == true) mask |= (byte)(1 << (i + 1));
            int mps = int.TryParse(meshPerSlot.Text, out int v) ? Math.Max(0, v) : 0;
            tcs.TrySetResult(new AddModelSpec(keyBox.Text?.Trim() ?? "", mask, mps, new List<string>(picked)));
            win.Close();
        };
        cancelButton.Click += (_, _) =>
        {
            tcs.TrySetResult(null);
            win.Close();
        };
        win.Closing += (_, _) => tcs.TrySetResult(null);
        await win.ShowDialog(_window);
        return await tcs.Task;
    }

    private void ShowReport(string title, string body)
    {
        if (_window is null) return;
        var box = new TextBox
        {
            Text = body,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            BorderThickness = default,
        };
        var win = new Window
        {
            Title = title,
            Width = 760,
            Height = 520,
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = box,
            },
        };
        win.Show(_window);
    }

    private Task ShowErrorAsync(string title, Exception ex)
    {
        ShowReport(title, ex.ToString());
        return Task.CompletedTask;
    }
}
