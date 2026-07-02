using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Documents;
using MyNotebook.App.Services;
using MyNotebook.Core.Models;
using MyNotebook.Core.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Microsoft.UI.Text;
using Microsoft.Web.WebView2.Core;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MyNotebook.App;

/// <summary>A panel that shows a west-east resize cursor (column splitter).</summary>
public partial class ColumnSizer : Grid
{
    public ColumnSizer() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
}

public sealed partial class MainWindow : Window
{
    private readonly INoteService _notes;
    private readonly IOcrService _ocr;
    private readonly IPathService _paths;
    private readonly ISettingsService _settings;
    private readonly IStorageService _storage;

    private readonly ObservableCollection<ThreadCard> _cards = new();
    private Note? _current;
    private bool _loadingNote;

    // WebView2 HTML editor (the live note body)
    private bool _webReady;
    private bool _webNavStarted;
    private Note? _pendingWebNote;

    // Distraction-free focus mode
    private bool _focusMode;
    private GridLength _savedSidebarWidth = new(260);

    // Search: term to scroll-to/highlight after opening a result
    private string _jumpTerm = "";
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _searchTimer;
    private string _pendingSearch = "";

    // Optimization caches
    private bool _wikiDirty = true;                       // rebuild [[ ]] list only when notes change
    private readonly Dictionary<long, long> _lastSnapshot = new();   // noteId -> last version time (throttle)

    // Voice reader (TTS) + audio recording
    private Windows.Media.Playback.MediaPlayer? _ttsPlayer;
    private Windows.Media.Capture.MediaCapture? _capture;
    private bool _recording;
    private string _recRel = "";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Folder-rail (tree) state
    private readonly Dictionary<TreeViewNode, NodeItem> _info = new();
    private TreeViewNode? _unfiledNode;
    private TreeViewNode? _allNotesNode;

    // Note-list pane state
    private readonly System.Collections.ObjectModel.ObservableCollection<NoteListRow> _noteRows = new();
    private enum ListFilter { AllNotes, Folder, Unfiled, SmartFolder, Trash, Tag }
    private ListFilter _filter = ListFilter.AllNotes;
    private long _filterId;          // folder id (Folder) or tag id (Tag)
    private string _filterQuery = "";
    private string _filterTitle = "All Notes";
    private string _sortMode = "modified";   // modified | created | title
    private bool _selectMode;                // checkbox (touch) multi-select via the Select button
    private bool _suppressOpen;              // guards programmatic selection from re-opening a note

    public MainWindow(INoteService notes, IOcrService ocr, IPathService paths, ISettingsService settings, IStorageService storage)
    {
        _notes = notes;
        _ocr = ocr;
        _paths = paths;
        _settings = settings;
        _storage = storage;

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Title = "My Notebook";
        SetWindowIcon();
        RestoreWindowPlacement();

        ThreadCards.ItemsSource = _cards;
        NoteList.ItemsSource = _noteRows;
        _listSavedWidth = Math.Clamp(_settings.Current.SidebarWidth, 230, 600);
        _railSavedWidth = Math.Clamp(_settings.Current.RailWidth, 150, 360);
        NoteListColumn.Width = new GridLength(_listSavedWidth);
        RailColumn.Width = new GridLength(_railSavedWidth);
        UpdateDrawerChrome();

        ApplyTheme();
        ApplyBackdrop();
        ApplyThemeSurfaces();       // tint chrome to match the saved theme
        RegisterAccelerators();
        BuildTree();

        InstallSubclass();
        AddTrayIcon();
        AppWindow.Closing += OnAppWindowClosing;
        if (_settings.Current.QuickNoteHotkey) TryEnableQuickNoteHotkey(true);

        // Pre-warm the editor WebView2 in the background so the first note opens instantly.
        _ = EnsureWebEditorAsync();

        if (!_ocr.IsAvailable)
            PasteHint.Message = "Paste screenshots with Ctrl+V. (No OCR language pack found — " +
                                "image text won't be searchable until you install one.)";
    }

    private void SetWindowIcon()
    {
        var iconPath = IconPath();
        if (!File.Exists(iconPath)) return;
        AppWindow.SetIcon(iconPath);
    }

    private static string IconPath() => Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

    // The search box lives in the title bar's drag region; mark its rectangle as a
    // passthrough so it receives pointer input instead of dragging the window.
    private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSearchPassthrough();
    private void SearchBox_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSearchPassthrough();

    private void TitleLeft_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSearchPassthrough();

    private void UpdateSearchPassthrough()
    {
        try
        {
            if (SearchBox.XamlRoot is null || SearchBox.ActualWidth < 1) return;
            var scale = SearchBox.XamlRoot.RasterizationScale;
            var rects = new List<Windows.Graphics.RectInt32> { RectFor(SearchBox, scale) };
            if (TitleLeft.ActualWidth > 1) rects.Add(RectFor(TitleLeft, scale));
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                .SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
        }
        catch { }
    }

    private static Windows.Graphics.RectInt32 RectFor(FrameworkElement el, double scale)
    {
        var p = el.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        return new Windows.Graphics.RectInt32(
            (int)Math.Round(p.X * scale), (int)Math.Round(p.Y * scale),
            (int)Math.Round(el.ActualWidth * scale), (int)Math.Round(el.ActualHeight * scale));
    }

    // ================================================================ Sidebar
    private void BuildTree()
    {
        _wikiDirty = true;   // structural change → refresh [[ ]] autocomplete list
        SidebarTree.RootNodes.Clear();
        _info.Clear();

        _allNotesNode = MakeNode(new NodeItem { Kind = NodeKind.AllNotes, Title = "All Notes", Glyph = "🗂" });
        SidebarTree.RootNodes.Add(_allNotesNode);

        var folders = _notes.ListFolders();
        foreach (var f in folders.Where(f => f.ParentId is null))
            SidebarTree.RootNodes.Add(BuildFolderNode(f, folders));

        _unfiledNode = MakeNode(new NodeItem { Kind = NodeKind.Unfiled, Title = "Unfiled", Glyph = "🗒" });
        SidebarTree.RootNodes.Add(_unfiledNode);

        var group = MakeNode(new NodeItem { Kind = NodeKind.Group, Title = "Smart Folders", Glyph = "🔎" });
        group.IsExpanded = true;
        foreach (var s in _notes.ListSavedSearches())
            group.Children.Add(MakeNode(new NodeItem { Kind = NodeKind.SmartFolder, Query = s.Query, Title = s.Name, Glyph = "🔎" }));
        SidebarTree.RootNodes.Add(group);

        var trashCount = _notes.GetStats().DeletedNotes;
        SidebarTree.RootNodes.Add(MakeNode(new NodeItem { Kind = NodeKind.Trash, Title = $"Trash ({trashCount})", Glyph = "🗑" }));

        HighlightCurrentFilterNode();   // re-select the node for the active filter
        PopulateNoteList();             // refresh the note list to match
    }

    private TreeViewNode BuildFolderNode(Folder f, IReadOnlyList<Folder> all)
    {
        var node = MakeNode(new NodeItem { Kind = NodeKind.Folder, Id = f.Id, Title = f.Name, Glyph = "📁" });
        node.IsExpanded = true;
        foreach (var sub in all.Where(x => x.ParentId == f.Id))
            node.Children.Add(BuildFolderNode(sub, all));
        return node;
    }

    private TreeViewNode MakeNode(NodeItem item)
    {
        var node = new TreeViewNode { Content = item };   // bound via ItemTemplate to DisplayText
        _info[node] = item;
        return node;
    }

    // ------------------------------------------------------------ Note list
    private void ApplyFilter(ListFilter f, long id, string title, string query)
    {
        _filter = f; _filterId = id; _filterTitle = title; _filterQuery = query;
        if (_selectMode) ExitSelectMode();
        HighlightCurrentFilterNode();
        PopulateNoteList();
    }

    private void PopulateNoteList()
    {
        IEnumerable<Note> notes = _filter switch
        {
            ListFilter.Folder => _notes.ListNotes(_filterId),
            ListFilter.Unfiled => _notes.ListNotes().Where(n => n.FolderId is null),
            ListFilter.SmartFolder => _notes.Search(_filterQuery, 300)
                                            .Select(h => _notes.GetNote(h.NoteId)).Where(n => n is not null).Select(n => n!),
            ListFilter.Trash => _notes.ListTrash(),
            ListFilter.Tag => _notes.ListNotesWithTag(_filterId),
            _ => _notes.ListNotes(),
        };

        // Pinned float to the top (except in Trash), then by the chosen sort key.
        bool trash = _filter == ListFilter.Trash;
        var ordered = (_sortMode switch
        {
            "created" => notes.OrderByDescending(n => trash || n.Pinned).ThenByDescending(n => n.CreatedAt),
            "title" => notes.OrderByDescending(n => trash || n.Pinned).ThenBy(n => EffectiveTitle(n), StringComparer.OrdinalIgnoreCase),
            _ => notes.OrderByDescending(n => trash || n.Pinned).ThenByDescending(n => n.UpdatedAt),
        }).ToList();
        // The OrderByDescending(pinned) above also pulls non-pinned together; reassert pin-first.
        if (!trash) ordered = ordered.OrderByDescending(n => n.Pinned).ToList();

        _noteRows.Clear();
        foreach (var n in ordered)
        {
            var t = EffectiveTitle(n);
            _noteRows.Add(new NoteListRow
            {
                Id = n.Id, Type = n.Type, Pinned = n.Pinned && !trash,
                Title = string.IsNullOrEmpty(t) ? "(untitled)" : t,
                Subtitle = NoteSubtitle(n),
            });
        }

        NoteListTitle.Text = _filterTitle;
        NoteListCount.Text = ordered.Count == 1 ? "1 note" : $"{ordered.Count} notes";
        NoteListEmpty.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_current is not null) SelectNoteInList(_current.Id);
    }

    private string NoteSubtitle(Note n)
    {
        var ts = _sortMode == "created" ? n.CreatedAt : n.UpdatedAt;
        var when = DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime.ToString("MMM d");
        var snippet = FirstLine(n.BodyPlain);
        if (string.Equals(snippet, EffectiveTitle(n), StringComparison.Ordinal)) snippet = "";
        return snippet.Length > 0 ? $"{when} · {snippet}" : when;
    }

    private void SelectNoteInList(long id)
    {
        if (_selectMode) return;
        var row = _noteRows.FirstOrDefault(r => r.Id == id);
        if (row is null || ReferenceEquals(NoteList.SelectedItem, row)) return;
        _suppressOpen = true;            // highlight only — don't re-trigger an open
        NoteList.SelectedItem = row;
        _suppressOpen = false;
    }

    private void HighlightCurrentFilterNode()
    {
        TreeViewNode? match = _filter switch
        {
            ListFilter.AllNotes => _allNotesNode,
            ListFilter.Unfiled => _unfiledNode,
            _ => _info.FirstOrDefault(kv =>
                    (_filter == ListFilter.Folder && kv.Value.Kind == NodeKind.Folder && kv.Value.Id == _filterId) ||
                    (_filter == ListFilter.SmartFolder && kv.Value.Kind == NodeKind.SmartFolder && kv.Value.Query == _filterQuery) ||
                    (_filter == ListFilter.Trash && kv.Value.Kind == NodeKind.Trash)).Key,
        };
        if (match is not null) SidebarTree.SelectedNode = match;
    }

    private void SidebarTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node || !_info.TryGetValue(node, out var item)) return;
        switch (item.Kind)
        {
            case NodeKind.AllNotes:    ApplyFilter(ListFilter.AllNotes, 0, "All Notes", ""); break;
            case NodeKind.Folder:      ApplyFilter(ListFilter.Folder, item.Id, item.Title, ""); break;
            case NodeKind.Unfiled:     ApplyFilter(ListFilter.Unfiled, 0, "Unfiled", ""); break;
            case NodeKind.SmartFolder: ApplyFilter(ListFilter.SmartFolder, 0, item.Title, item.Query); break;
            case NodeKind.Trash:       ApplyFilter(ListFilter.Trash, 0, "Trash", ""); break;
        }
    }

    // Folders are realized eagerly and the special nodes are leaves, so there is nothing to lazy-load.
    private void SidebarTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args) { }

    // Note deletion lives on the note list now (the tree holds only folders).
    private void SidebarTree_KeyDown(object sender, KeyRoutedEventArgs e) { }

    // ------------------------------------------------------- Note-list events
    // Extended selection: a single click opens the note; Ctrl/Shift-click builds a
    // multi-selection and reveals the action bar. The Select button offers the same
    // via tappable checkboxes (Multiple mode) for touch/mouse-only use.
    private void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOpen) return;
        int count = NoteList.SelectedItems.Count;

        if (_selectMode) { UpdateSelectCount(); return; }   // checkbox mode: bar already shown

        if (count >= 2)
        {
            if (SelectBar.Visibility != Visibility.Visible) BuildBulkMoveFlyout();
            SelectBar.Visibility = Visibility.Visible;
            UpdateSelectCount();
        }
        else
        {
            SelectBar.Visibility = Visibility.Collapsed;
            if (count == 1 && NoteList.SelectedItem is NoteListRow row && _current?.Id != row.Id)
                ShowNote(row.Id);
        }
    }

    private void NoteList_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not NoteListRow row) return;
        if (!_selectMode) NoteList.SelectedItem = row;
        BuildNoteMenu(row.Id, _filter == ListFilter.Trash).ShowAt(NoteList,
            new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(NoteList) });
    }

    private async void NoteList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete) return;
        var ids = SelectedNoteIds();
        if (ids.Count > 0) await DeleteNotes(ids, _filter == ListFilter.Trash);
    }

    private List<long> SelectedNoteIds() => NoteList.SelectedItems.OfType<NoteListRow>().Select(r => r.Id).ToList();

    // ---- select mode ----
    private void ToggleSelectMode_Click(object sender, RoutedEventArgs e)
    {
        if (_selectMode) ExitSelectMode(); else EnterSelectMode();
    }

    // "Done": clears whatever multi-selection is active (checkbox mode or Ctrl/Shift) and hides the bar.
    private void CloseSelect_Click(object sender, RoutedEventArgs e)
    {
        if (_selectMode) { ExitSelectMode(); return; }
        SelectBar.Visibility = Visibility.Collapsed;
        _suppressOpen = true;
        NoteList.SelectedItems.Clear();
        _suppressOpen = false;
        if (_current is not null) SelectNoteInList(_current.Id);
    }

    private void EnterSelectMode()
    {
        _selectMode = true;
        NoteList.SelectionMode = Microsoft.UI.Xaml.Controls.ListViewSelectionMode.Multiple;  // tappable checkboxes
        SelectBar.Visibility = Visibility.Visible;
        BuildBulkMoveFlyout();
        UpdateSelectCount();
    }

    private void ExitSelectMode()
    {
        _selectMode = false;
        SelectBar.Visibility = Visibility.Collapsed;
        _suppressOpen = true;
        NoteList.SelectionMode = Microsoft.UI.Xaml.Controls.ListViewSelectionMode.Extended;
        NoteList.SelectedItems.Clear();
        _suppressOpen = false;
        if (_current is not null) SelectNoteInList(_current.Id);
    }

    private void UpdateSelectCount()
    {
        int c = NoteList.SelectedItems.Count;
        SelectCount.Text = c == 1 ? "1 selected" : $"{c} selected";
    }

    private void BuildBulkMoveFlyout()
    {
        BulkMoveFlyout.Items.Clear();
        var unfiled = new MenuFlyoutItem { Text = "Unfiled" };
        unfiled.Click += (_, _) => BulkMoveTo(null);
        BulkMoveFlyout.Items.Add(unfiled);
        foreach (var f in _notes.ListFolders())
        {
            var fid = f.Id;
            var mi = new MenuFlyoutItem { Text = f.Name };
            mi.Click += (_, _) => BulkMoveTo(fid);
            BulkMoveFlyout.Items.Add(mi);
        }
    }

    private void BulkMoveTo(long? folderId)
    {
        foreach (var id in SelectedNoteIds()) _notes.MoveNoteToFolder(id, folderId);
        ExitSelectMode();
        PopulateNoteList();
    }

    private void BulkPin_Click(object sender, RoutedEventArgs e)
    {
        var ids = SelectedNoteIds();
        if (ids.Count == 0) return;
        bool pinAll = ids.Select(id => _notes.GetNote(id)).Any(n => n is { Pinned: false });
        foreach (var id in ids) _notes.SetPinned(id, pinAll);
        ExitSelectMode();
        PopulateNoteList();
    }

    private async void BulkDelete_Click(object sender, RoutedEventArgs e)
    {
        var ids = SelectedNoteIds();
        if (ids.Count > 0) await DeleteNotes(ids, _filter == ListFilter.Trash);
    }

    private async Task DeleteNotes(IReadOnlyList<long> ids, bool forever)
    {
        if (_settings.Current.ConfirmBeforeDelete || forever)
        {
            var d = new ContentDialog
            {
                Title = forever ? "Delete forever?" : (ids.Count == 1 ? "Delete note?" : $"Delete {ids.Count} notes?"),
                Content = forever
                    ? $"Permanently delete {ids.Count} item(s) and their images. This cannot be undone."
                    : (ids.Count == 1 ? "Move this note to the trash?" : $"Move {ids.Count} notes to the trash?"),
                PrimaryButtonText = forever ? "Delete forever" : "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        }
        foreach (var id in ids)
        {
            if (forever)
                foreach (var rel in _notes.DeleteNoteForever(id))
                    { try { System.IO.File.Delete(_paths.ToAbsolute(rel)); } catch { } }
            else
                _notes.SoftDeleteNote(id);
            if (_current?.Id == id) { HideDetailPanes(); EmptyState.Visibility = Visibility.Visible; _current = null; }
        }
        if (_selectMode) ExitSelectMode();
        BuildTree();   // refresh trash count + the list
    }

    // ---- sort ----
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string tag }) { _sortMode = tag; PopulateNoteList(); }
    }

    // ---- drag a note (or the whole selection) onto a folder in the rail ----
    private void NoteList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var dragged = e.Items.OfType<NoteListRow>().Select(r => r.Id).ToList();
        var sel = SelectedNoteIds();
        var ids = (_selectMode && sel.Count > 0) ? sel.Union(dragged).Distinct().ToList() : dragged;
        if (ids.Count == 0) { e.Cancel = true; return; }
        e.Data.SetText("mnb-notes:" + string.Join(",", ids));
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void Rail_DragOver(object sender, DragEventArgs e)
    {
        var node = NodeFromSource(e.OriginalSource);
        bool ok = node is not null && _info.TryGetValue(node, out var it)
                  && it.Kind is NodeKind.Folder or NodeKind.Unfiled or NodeKind.AllNotes;
        e.AcceptedOperation = ok ? DataPackageOperation.Move : DataPackageOperation.None;
    }

    private async void Rail_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        var node = NodeFromSource(e.OriginalSource);
        if (node is null || !_info.TryGetValue(node, out var it)) return;
        long? target;
        if (it.Kind == NodeKind.Folder) target = it.Id;
        else if (it.Kind is NodeKind.Unfiled or NodeKind.AllNotes) target = null;
        else return;

        var text = await e.DataView.GetTextAsync();
        if (!text.StartsWith("mnb-notes:")) return;
        var ids = text["mnb-notes:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => long.TryParse(s, out var v) ? v : 0).Where(v => v > 0);
        foreach (var id in ids) _notes.MoveNoteToFolder(id, target);
        if (_selectMode) ExitSelectMode();
        PopulateNoteList();
    }

    // ---- per-row context menu (notes + trash) ----
    private MenuFlyout BuildNoteMenu(long id, bool inTrash)
    {
        var menu = new MenuFlyout();
        if (inTrash)
        {
            AddItem(menu, "Restore", () => RestoreFromTrash(id));
            AddItem(menu, "Delete forever", () => _ = DeleteForeverPrompt(id, NoteTitleOf(id)));
            return menu;
        }
        var note = _notes.GetNote(id);
        bool pinned = note?.Pinned ?? false;
        AddItem(menu, "Open", () => ShowNote(id));
        AddItem(menu, pinned ? "Unpin" : "Pin", () => { _notes.SetPinned(id, !pinned); PopulateNoteList(); });
        AddItem(menu, "Rename", () => _ = RenameNotePrompt(id));
        var moveTo = new MenuFlyoutSubItem { Text = "Move to" };
        var unfiled = new MenuFlyoutItem { Text = "Unfiled" };
        unfiled.Click += (_, _) => MoveNote(id, null);
        moveTo.Items.Add(unfiled);
        foreach (var f in _notes.ListFolders())
        {
            var fid = f.Id;
            var mi = new MenuFlyoutItem { Text = f.Name };
            mi.Click += (_, _) => MoveNote(id, fid);
            moveTo.Items.Add(mi);
        }
        menu.Items.Add(moveTo);
        var export = new MenuFlyoutSubItem { Text = "Export" };
        void Exp(string t, Action a) { var mi = new MenuFlyoutItem { Text = t }; mi.Click += (_, _) => a(); export.Items.Add(mi); }
        Exp("PDF…", () => _ = ExportNotePdf(id));
        Exp("Web page (HTML)…", () => _ = ExportNoteHtml(id));
        Exp("Word (.docx)…", () => _ = ExportNoteWord(id));
        menu.Items.Add(export);
        AddItem(menu, "Print…", () => _ = PrintNote(id));
        AddItem(menu, "Version history…", () => _ = ShowVersionHistory(id));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(menu, "Delete", () => _ = DeleteNotePrompt(id, NoteTitleOf(id)));
        return menu;
    }

    private string NoteTitleOf(long id)
    {
        var n = _notes.GetNote(id);
        if (n is null) return "";
        var t = EffectiveTitle(n);
        return string.IsNullOrEmpty(t) ? "(untitled)" : t;
    }

    // ---------------------------------------------------- Sidebar context menu
    private void SidebarTree_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        var node = NodeFromSource(e.OriginalSource);
        if (node is null || !_info.TryGetValue(node, out var item)) return;
        SidebarTree.SelectedNode = node;
        BuildContextMenu(item).ShowAt(SidebarTree, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = e.GetPosition(SidebarTree)
        });
    }

    private TreeViewNode? NodeFromSource(object src)
    {
        var fe = src as FrameworkElement;
        while (fe is not null)
        {
            if (fe.DataContext is TreeViewNode n && _info.ContainsKey(n)) return n;
            fe = (fe.Parent as FrameworkElement) ?? (VisualTreeHelper.GetParent(fe) as FrameworkElement);
        }
        return null;
    }

    private MenuFlyout BuildContextMenu(NodeItem item)
    {
        var menu = new MenuFlyout();
        switch (item.Kind)
        {
            case NodeKind.Note:
                AddItem(menu, "Open", () => ShowNote(item.Id));
                AddItem(menu, item.Pinned ? "Unpin" : "Pin", () => TogglePin(item));
                AddItem(menu, "Rename", () => _ = RenameNotePrompt(item.Id));
                var moveTo = new MenuFlyoutSubItem { Text = "Move to" };
                var unfiled = new MenuFlyoutItem { Text = "Unfiled" };
                unfiled.Click += (_, _) => MoveNote(item.Id, null);
                moveTo.Items.Add(unfiled);
                foreach (var f in _notes.ListFolders())
                {
                    var mi = new MenuFlyoutItem { Text = f.Name };
                    var fid = f.Id;
                    mi.Click += (_, _) => MoveNote(item.Id, fid);
                    moveTo.Items.Add(mi);
                }
                menu.Items.Add(moveTo);
                var export = new MenuFlyoutSubItem { Text = "Export" };
                void Exp(string t, Action a) { var mi = new MenuFlyoutItem { Text = t }; mi.Click += (_, _) => a(); export.Items.Add(mi); }
                Exp("PDF…", () => _ = ExportNotePdf(item.Id));
                Exp("Web page (HTML)…", () => _ = ExportNoteHtml(item.Id));
                Exp("Word (.docx)…", () => _ = ExportNoteWord(item.Id));
                menu.Items.Add(export);
                AddItem(menu, "Print…", () => _ = PrintNote(item.Id));
                AddItem(menu, "Version history…", () => _ = ShowVersionHistory(item.Id));
                menu.Items.Add(new MenuFlyoutSeparator());
                AddItem(menu, "Delete", () => _ = DeleteNotePrompt(item.Id, item.DisplayTitle));
                break;

            case NodeKind.Folder:
                AddItem(menu, "New note here", () => CreateNoteInFolder(item.Id));
                AddItem(menu, "New subfolder", () => _ = NewFolderPrompt(item.Id));
                AddItem(menu, "Rename", () => _ = RenameFolderPrompt(item.Id, item.Title));
                AddItem(menu, "Export folder to PDF…", () => _ = ExportFolderPdf(item.Id, item.Title));
                menu.Items.Add(new MenuFlyoutSeparator());
                AddItem(menu, "Delete folder", () => _ = DeleteFolderPrompt(item.Id, item.Title));
                break;

            case NodeKind.Unfiled:
                AddItem(menu, "New note here", () => CreateAndOpen(NoteType.Note, "New note"));
                break;

            case NodeKind.TrashNote:
                AddItem(menu, "Restore", () => RestoreFromTrash(item.Id));
                AddItem(menu, "Delete forever", () => _ = DeleteForeverPrompt(item.Id, item.DisplayTitle));
                break;
        }
        return menu;
    }

    private void RestoreFromTrash(long noteId)
    {
        _notes.RestoreNote(noteId);
        if (_current?.Id == noteId) { HideDetailPanes(); EmptyState.Visibility = Visibility.Visible; _current = null; }
        BuildTree();
    }

    private async Task DeleteForeverPrompt(long noteId, string title)
    {
        var dlg = new ContentDialog
        {
            Title = "Delete forever?",
            Content = $"Permanently delete “{title}” and its images. This cannot be undone.",
            PrimaryButtonText = "Delete forever",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var rel in _notes.DeleteNoteForever(noteId))
        {
            try { File.Delete(_paths.ToAbsolute(rel)); } catch { }
        }
        if (_current?.Id == noteId) { HideDetailPanes(); EmptyState.Visibility = Visibility.Visible; _current = null; }
        BuildTree();
    }

    private static void AddItem(MenuFlyout menu, string text, Action action)
    {
        var mi = new MenuFlyoutItem { Text = text };
        mi.Click += (_, _) => action();
        menu.Items.Add(mi);
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e) => _ = NewFolderPrompt(null);

    private async Task NewFolderPrompt(long? parentId)
    {
        var name = await PromptTextAsync(parentId is null ? "New folder" : "New subfolder", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        _notes.CreateFolder(name.Trim(), parentId);
        BuildTree();
    }

    private void CreateNoteInFolder(long folderId)
    {
        var note = _notes.CreateNote("New note", NoteType.Note, folderId);
        var name = _notes.ListFolders().FirstOrDefault(f => f.Id == folderId)?.Name ?? "Folder";
        ApplyFilter(ListFilter.Folder, folderId, name, "");
        ShowNote(note.Id);
    }

    private async Task RenameNotePrompt(long noteId)
    {
        var note = _notes.GetNote(noteId);
        if (note is null) return;
        var name = await PromptTextAsync("Rename note", note.Title);
        if (name is null) return;
        note.Title = name;
        _notes.UpdateNote(note);
        if (_current?.Id == noteId) TitleBox.Text = name;
        UpdateNodeTitle(note);
    }

    private async Task RenameFolderPrompt(long folderId, string current)
    {
        var name = await PromptTextAsync("Rename folder", current);
        if (string.IsNullOrWhiteSpace(name)) return;
        _notes.RenameFolder(folderId, name.Trim());
        BuildTree();
    }

    private async Task DeleteNotePrompt(long noteId, string title)
    {
        if (_settings.Current.ConfirmBeforeDelete)
        {
            var dlg = new ContentDialog
            {
                Title = "Delete note?",
                Content = $"Move “{title}” to the trash?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        }
        _notes.SoftDeleteNote(noteId);
        if (_current?.Id == noteId) { HideDetailPanes(); EmptyState.Visibility = Visibility.Visible; _current = null; }
        BuildTree();
    }

    private async Task DeleteFolderPrompt(long folderId, string name)
    {
        var dlg = new ContentDialog
        {
            Title = "Delete folder?",
            Content = $"Delete “{name}” and move all notes inside it (and its subfolders) to the trash?",
            PrimaryButtonText = "Delete folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _notes.DeleteFolder(folderId);
        if (_current is not null && _notes.GetNote(_current.Id) is { Deleted: true })
        { HideDetailPanes(); EmptyState.Visibility = Visibility.Visible; _current = null; }
        BuildTree();
    }

    private void TogglePin(NodeItem item)
    {
        bool pin = !item.Pinned;
        _notes.SetPinned(item.Id, pin);
        item.Pinned = pin;   // INotifyPropertyChanged updates the 📌 prefix in the tree
    }

    private void MoveNote(long noteId, long? folderId)
    {
        _notes.MoveNoteToFolder(noteId, folderId);
        PopulateNoteList();   // the note may have left the current filter
    }

    private async Task<string?> PromptTextAsync(string title, string initial)
    {
        var box = new TextBox { Text = initial ?? "" };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var dlg = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    // -------------------------------------------------------------- PDF export
    private async Task ExportNotePdf(long noteId)
    {
        var note = _notes.GetNote(noteId);
        if (note is null) return;
        var file = await PickSaveAsync(SafeFile(EffectiveTitle(note)), ".pdf", "PDF document");
        if (file is null) return;
        try
        {
            PdfExporter.Export(new[] { ToDoc(note) }, file.Path);
            await ShowInfoAsync("Exported to PDF", file.Path);
        }
        catch (Exception ex) { await ShowInfoAsync("PDF export failed", ex.Message); }
    }

    private async Task ExportNoteHtml(long noteId)
    {
        var note = _notes.GetNote(noteId);
        if (note is null) return;
        var file = await PickSaveAsync(SafeFile(EffectiveTitle(note)), ".html", "Web page");
        if (file is null) return;
        try
        {
            await File.WriteAllTextAsync(file.Path, BuildStandaloneHtml(note), new UTF8Encoding(false));
            await ShowInfoAsync("Exported web page", file.Path);
        }
        catch (Exception ex) { await ShowInfoAsync("Export failed", ex.Message); }
    }

    private async Task ExportNoteWord(long noteId)
    {
        var note = _notes.GetNote(noteId);
        if (note is null) return;
        var file = await PickSaveAsync(SafeFile(EffectiveTitle(note)), ".docx", "Word document");
        if (file is null) return;
        try
        {
            await WordExporter.ExportAsync(NoteContentHtml(note), file.Path);
            await ShowInfoAsync("Exported Word document", file.Path);
        }
        catch (Exception ex) { await ShowInfoAsync("Export failed", ex.Message); }
    }

    private async Task PrintNote(long noteId)
    {
        ShowNote(noteId);   // make sure the note is loaded in the editor WebView
        try { await EnsureWebEditorAsync(); } catch { }
        // Give the document a beat to render, then open the print dialog.
        await Task.Delay(250);
        try { NoteWeb.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); }
        catch (Exception ex) { await ShowInfoAsync("Print unavailable", ex.Message); }
    }

    // ------------------------------------------------------- Version history
    /// <summary>Snapshot the note if the last snapshot is older than a few minutes.</summary>
    private void MaybeSnapshot(Note note)
    {
        if (note.Type != NoteType.Note || string.IsNullOrWhiteSpace(note.BodyPlain)) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // In-memory throttle (no DB query per save). The persisted prune-to-50 is unaffected.
        if (_lastSnapshot.TryGetValue(note.Id, out var last) && now - last < 3 * 60 * 1000) return;
        _notes.SaveNoteVersion(note.Id, note.Title, note.BodyRtf ?? "", note.BodyPlain ?? "");
        _lastSnapshot[note.Id] = now;
    }

    private async Task ShowVersionHistory(long noteId)
    {
        var note = _notes.GetNote(noteId);
        if (note is null) return;
        var versions = _notes.ListNoteVersions(noteId);
        if (versions.Count == 0)
        {
            await ShowInfoAsync("Version history", "No earlier versions yet — history builds up as you edit a note.");
            return;
        }

        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var previewScroll = new ScrollViewer { Content = preview };
        Grid.SetColumn(previewScroll, 1);

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single };
        foreach (var v in versions)
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(v.CreatedAt).LocalDateTime.ToString("ddd, MMM d  h:mm tt");
            list.Items.Add(new ListViewItem { Content = when, Tag = v });
        }
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListViewItem { Tag: NoteVersion v })
                preview.Text = string.IsNullOrWhiteSpace(v.BodyPlain) ? "(empty)" : v.BodyPlain;
        };
        list.SelectedIndex = 0;

        var grid = new Grid { Width = 640, Height = 360, ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(list);
        grid.Children.Add(previewScroll);

        var dlg = new ContentDialog
        {
            Title = $"Version history — {EffectiveTitle(note)}",
            Content = grid,
            PrimaryButtonText = "Restore selected",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary &&
            list.SelectedItem is ListViewItem { Tag: NoteVersion chosen })
        {
            // Snapshot the current state first so the restore itself is undoable.
            _notes.SaveNoteVersion(note.Id, note.Title, note.BodyRtf ?? "", note.BodyPlain ?? "");
            note.Title = chosen.Title;
            note.BodyRtf = chosen.BodyRtf;
            note.BodyPlain = chosen.BodyPlain;
            _notes.UpdateNote(note);
            UpdateNodeTitle(note);
            if (_current?.Id == note.Id) ShowNote(note.Id);
        }
    }

    /// <summary>Title + date + body (images embedded as data URIs). Shared by HTML and Word export.</summary>
    private string NoteContentHtml(Note note)
    {
        var title = WebUtility.HtmlEncode(EffectiveTitle(note));
        var date = WebUtility.HtmlEncode(
            DateTimeOffset.FromUnixTimeMilliseconds(note.CreatedAt).LocalDateTime.ToString("dddd, MMMM d, yyyy  h:mm tt"));

        string body;
        if (note.Type == NoteType.Thread)
        {
            var sb = new StringBuilder();
            foreach (var img in _notes.ListImages(note.Id))
            {
                sb.Append($"<p><img src=\"{ImageDataUri(_paths.ToAbsolute(img.RelPath))}\" /></p>");
                if (!string.IsNullOrWhiteSpace(img.Caption))
                    sb.Append($"<p><i>{WebUtility.HtmlEncode(img.Caption)}</i></p>");
            }
            body = sb.ToString();
        }
        else
        {
            body = EmbedImages(HtmlForNote(note));
        }
        return $"<h1>{title}</h1><p style=\"color:#888\">{date}</p>{body}";
    }

    /// <summary>A self-contained HTML document for a note (the "publish as web page" output).</summary>
    private string BuildStandaloneHtml(Note note) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>" +
        WebUtility.HtmlEncode(EffectiveTitle(note)) + "</title><style>" +
        "body{font-family:Calibri,'Segoe UI','Myanmar Text',sans-serif;font-size:16px;line-height:1.7;" +
        "color:#1c1c1c;max-width:760px;margin:32px auto;padding:0 24px;}" +
        "h1{font-size:1.7em;font-weight:600;margin:0 0 4px;}img{max-width:100%;height:auto;border-radius:4px;}" +
        "</style></head><body>" + NoteContentHtml(note) + "</body></html>";

    private string EmbedImages(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "https://notes\\.local/([^\"'\\s>]+)", m =>
        {
            try
            {
                var rel = Uri.UnescapeDataString(m.Groups[1].Value).Replace('/', Path.DirectorySeparatorChar);
                return ImageDataUri(_paths.ToAbsolute(rel)) is { Length: > 0 } d ? d : m.Value;
            }
            catch { return m.Value; }
        });

    private static string ImageDataUri(string absPath)
    {
        try
        {
            if (!File.Exists(absPath)) return "";
            var ext = Path.GetExtension(absPath).TrimStart('.').ToLowerInvariant();
            var mime = ext is "jpg" or "jpeg" ? "image/jpeg" : ext == "gif" ? "image/gif" : "image/png";
            return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(absPath))}";
        }
        catch { return ""; }
    }

    private async Task ExportFolderPdf(long folderId, string folderName)
    {
        var ids = FolderAndDescendants(folderId);
        var notes = new List<Note>();
        foreach (var fid in ids) notes.AddRange(_notes.ListNotes(fid));
        if (notes.Count == 0) { await ShowInfoAsync("Nothing to export", "This folder has no notes."); return; }

        var file = await PickSaveAsync(SafeFile(folderName), ".pdf", "PDF document");
        if (file is null) return;
        try
        {
            PdfExporter.Export(notes.Select(ToDoc).ToList(), file.Path);
            await ShowInfoAsync("Exported folder to PDF", $"{notes.Count} notes → {file.Path}");
        }
        catch (Exception ex) { await ShowInfoAsync("PDF export failed", ex.Message); }
    }

    private PdfExporter.NoteDoc ToDoc(Note n)
    {
        var date = DateTimeOffset.FromUnixTimeMilliseconds(n.CreatedAt).LocalDateTime.ToString("dddd, MMMM d, yyyy  h:mm tt");
        var body = n.BodyPlain ?? "";
        if (n.Type == NoteType.Thread)
        {
            var sb = new StringBuilder(body);
            foreach (var img in _notes.ListImages(n.Id))
            {
                if (!string.IsNullOrWhiteSpace(img.Caption)) sb.AppendLine().Append("• ").Append(img.Caption);
                if (!string.IsNullOrWhiteSpace(img.OcrText)) sb.AppendLine().Append("   ").Append(img.OcrText);
            }
            body = sb.ToString();
        }
        return new PdfExporter.NoteDoc(EffectiveTitle(n), date, body);
    }

    private List<long> FolderAndDescendants(long folderId)
    {
        var all = _notes.ListFolders();
        var result = new List<long> { folderId };
        void Recurse(long id)
        {
            foreach (var f in all.Where(x => x.ParentId == id)) { result.Add(f.Id); Recurse(f.Id); }
        }
        Recurse(folderId);
        return result;
    }

    private async Task<StorageFile?> PickSaveAsync(string name, string ext, string typeName)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = name };
        picker.FileTypeChoices.Add(typeName, new List<string> { ext });
        InitializeWithWindow(picker);
        return await picker.PickSaveFileAsync();
    }

    private static string SafeFile(string s) =>
        string.Join("_", (string.IsNullOrWhiteSpace(s) ? "export" : s).Split(Path.GetInvalidFileNameChars()));

    // -------------------------------------------------------------------- Tags
    private void RefreshNoteTags()
    {
        NoteTagsList.Items.Clear();
        if (_current is null) return;
        foreach (var tag in _notes.ListTagsForNote(_current.Id))
            NoteTagsList.Items.Add(MakeTagChip(tag));
    }

    private FrameworkElement MakeTagChip(Tag tag)
    {
        var label = new Button
        {
            Content = new TextBlock { Text = "#" + tag.Name, FontSize = 12 },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 2, 0),
        };
        label.Click += (_, _) => FilterByTag(tag);

        var remove = new Button
        {
            Content = new TextBlock { Text = "✕", FontSize = 10, Opacity = 0.7 },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 2, 0),
        };
        remove.Click += (_, _) =>
        {
            if (_current is not null) { _notes.RemoveTagFromNote(_current.Id, tag.Id); RefreshNoteTags(); }
        };

        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(label);
        sp.Children.Add(remove);
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4, 0, 2, 0),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = sp,
        };
    }

    private void FilterByTag(Tag tag)
    {
        SidebarTree.SelectedNode = null;   // a tag view isn't a tree node
        ApplyFilter(ListFilter.Tag, tag.Id, $"#{tag.Name}", "");
    }

    private void AddTag_Click(object sender, RoutedEventArgs e) => AddCurrentTag();
    private void AddTagBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => AddCurrentTag();

    private void AddTagBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var q = (sender.Text ?? "").Trim().TrimStart('#');
        sender.ItemsSource = _notes.ListTags()
            .Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name).Take(8).ToList();
    }

    private void AddCurrentTag()
    {
        if (_current is null) return;
        var name = (AddTagBox.Text ?? "").Trim().TrimStart('#');
        if (name.Length == 0) return;
        var tag = _notes.EnsureTag(name);
        _notes.AddTagToNote(_current.Id, tag.Id);
        AddTagBox.Text = "";
        AddTagBox.ItemsSource = null;
        RefreshNoteTags();
    }

    // -------------------------------------------------- Drag notes into folders
    private void SidebarTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        // Only notes are draggable; cancel if any non-note is in the set.
        foreach (var obj in args.Items)
            if (obj is TreeViewNode n && _info.TryGetValue(n, out var it) && it.Kind != NodeKind.Note)
            { args.Cancel = true; return; }
    }

    private void SidebarTree_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        foreach (var obj in args.Items)
        {
            if (obj is not TreeViewNode node) continue;
            if (!_info.TryGetValue(node, out var item) || item.Kind != NodeKind.Note) continue;

            long? folderId = null;
            if (node.Parent is { } parent && _info.TryGetValue(parent, out var pItem))
                folderId = pItem.Kind == NodeKind.Folder ? pItem.Id : null;   // Folder -> that folder; Unfiled/root -> null
            _notes.MoveNoteToFolder(item.Id, folderId);
        }
    }

    // ----------------------------------------------------------- Show / edit
    private void ShowNote(long id)
    {
        var note = _notes.GetNote(id);
        if (note is null) return;
        StopTts();   // stop any read-aloud when switching notes
        _current = note;
        _loadingNote = true;

        HideDetailPanes();
        FindBar.Visibility = Visibility.Collapsed;
        if (note.Type == NoteType.Thread)
        {
            ThreadPane.Visibility = Visibility.Visible;
            ThreadTitleBox.Text = note.Title;
            LoadCards(note.Id);
            ThreadPane.Focus(FocusState.Programmatic);   // scope Ctrl+V paste to the thread pane
        }
        else
        {
            EditorPane.Visibility = Visibility.Visible;
            TitleBox.Text = note.Title;
            // OneNote-style stamp: creation date on one line, time on the next.
            var created = DateTimeOffset.FromUnixTimeMilliseconds(note.CreatedAt).LocalDateTime;
            DateLine.Text = created.ToString("dddd, MMMM d, yyyy");
            EditedLine.Text = created.ToString("h:mm tt");
            ApplyPaperStyle();          // page color + text contrast (title area)
            RefreshNoteTags();
            _ = EnsureWebEditorAsync();
            LoadNoteIntoWeb(note);      // load body into the contenteditable WebView2
            FadeInPaper();
        }
        _loadingNote = false;

        SelectNoteInList(id);
    }

    private void HideDetailPanes()
    {
        EditorPane.Visibility = Visibility.Collapsed;
        ThreadPane.Visibility = Visibility.Collapsed;
        SearchResultsPane.Visibility = Visibility.Collapsed;
        TimelinePane.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
    }

    private void LoadCards(long noteId)
    {
        _cards.Clear();
        foreach (var img in _notes.ListImages(noteId))
            _cards.Add(ThreadCard.From(img, _paths.ToAbsolute(img.RelPath)));
        RenumberCards();
    }

    /// <summary>Number the timeline nodes 1..N and mark the first/last for rail-line trimming.</summary>
    private void RenumberCards()
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            _cards[i].Number = i + 1;
            _cards[i].IsFirst = i == 0;
            _cards[i].IsLast = i == _cards.Count - 1;
        }
    }

    // ===================================================== WebView2 HTML editor
    // The note body is a contenteditable document hosted in a WebView2. It is the
    // only host that gives a single continuous document where images sit inline at
    // full resolution, are clickable, and flow naturally with the text. The body is
    // stored as HTML in the (reused) BodyRtf column; body_plain feeds FTS search.

    private async Task EnsureWebEditorAsync()
    {
        if (_webNavStarted) return;
        _webNavStarted = true;
        try
        {
            // Keep the browser cache/profile inside the portable data folder.
            var udf = Path.Combine(_paths.DataRoot, "WebView2");
            Directory.CreateDirectory(udf);
            var env = await CoreWebView2Environment.CreateWithOptionsAsync("", udf, new CoreWebView2EnvironmentOptions());
            await NoteWeb.EnsureCoreWebView2Async(env);

            var core = NoteWeb.CoreWebView2;
            // Serve attachment files: https://notes.local/<relpath> -> DataRoot\<relpath>.
            core.SetVirtualHostNameToFolderMapping("notes.local", _paths.DataRoot,
                CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.NavigateToString(EditorHtml);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("WebView2 init failed: " + ex);
        }
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        // JS posts JSON.stringify(obj), i.e. a STRING. WebMessageAsJson would double-encode
        // that string; TryGetWebMessageAsString gives us the raw JSON text to parse.
        WebMsg? m;
        try { m = JsonSerializer.Deserialize<WebMsg>(args.TryGetWebMessageAsString(), JsonOpts); }
        catch { return; }
        if (m is null) return;
        switch (m.Type)
        {
            case "ready":
                _webReady = true;
                if (_pendingWebNote is { } pend) { _pendingWebNote = null; LoadNoteIntoWeb(pend); }
                break;
            case "save":
                if (_current is not null && m.Id == _current.Id)   // ignore stragglers from a note we just left
                    SaveHtmlBody(m.Html ?? "", m.Text ?? "");
                break;
            case "img":
                _ = HandleWebPasteImage(m.Data ?? "");
                break;
            case "pasteimg":
                _ = HandleWebPasteRemoteImage(m.Src ?? "", m.Data ?? "");
                break;
            case "open":
                var rel = (m.Src ?? "").Replace('/', Path.DirectorySeparatorChar);
                if (rel.Length > 0) OpenImageViewer(_paths.ToAbsolute(rel));
                break;
            case "find":
                ToggleFindBar();
                break;
            case "focussearch":
                FocusSearch();
                break;
            case "togglefocus":
                SetFocusMode(!_focusMode);
                break;
            case "exitfocus":
                if (_focusMode) SetFocusMode(false);
                break;
            case "pasted":
                if (_settings.Current.PasteSourceUrl) _ = AppendPasteSourceAsync();
                ApplyAutoTitle(m.Text);
                break;
            case "opennote":
                if (m.Id > 0) ShowNote(m.Id);
                break;
            case "openurl":
                var u = m.Src ?? "";
                if (u.StartsWith("http://") || u.StartsWith("https://"))
                    try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
                break;
        }
    }

    /// <summary>If the clipboard text came from a web page, append "Source: url" after the paste.</summary>
    private async Task AppendPasteSourceAsync()
    {
        try
        {
            var dp = Clipboard.GetContent();
            if (!dp.Contains(StandardDataFormats.Html)) return;
            var html = await dp.GetHtmlFormatAsync();
            var m = System.Text.RegularExpressions.Regex.Match(html, @"SourceURL:(\S+)");
            if (!m.Success) return;
            var url = m.Groups[1].Value.Trim();
            if (!(url.StartsWith("http://") || url.StartsWith("https://"))) return;
            url = CleanTrackingUrl(url);
            if (NoteWeb.CoreWebView2 is not null)
                await NoteWeb.CoreWebView2.ExecuteScriptAsync($"appendSource({JsonSerializer.Serialize(url)})");
        }
        catch { }
    }

    private static readonly System.Text.RegularExpressions.Regex TrackingParam = new(
        @"^(utm_.*|fbclid|gclid|gclsrc|dclid|msclkid|igshid|mc_eid|mc_cid|mkt_tok|_hsenc|_hsmi|hsa_.*|vero_id|vero_conv|oly_enc_id|oly_anon_id|piwik_.*|pk_.*|yclid|_openstat|ref|ref_src|spm|triedRedirect)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Drop common tracking query params (utm_*, fbclid, …) from a URL, keeping path + fragment.</summary>
    private static string CleanTrackingUrl(string url)
    {
        int q = url.IndexOf('?');
        if (q < 0) return url;
        string head = url[..q], query = url[(q + 1)..], frag = "";
        int hash = query.IndexOf('#');
        if (hash >= 0) { frag = query[hash..]; query = query[..hash]; }
        var kept = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Where(p => !TrackingParam.IsMatch(p.Split('=', 2)[0]));
        var nq = string.Join("&", kept);
        return head + (nq.Length > 0 ? "?" + nq : "") + frag;
    }

    private void LoadNoteIntoWeb(Note note)
    {
        if (!_webReady || NoteWeb.CoreWebView2 is null) { _pendingWebNote = note; return; }
        var bg = PaperBackgroundColor();
        bool dark = IsDark(bg);
        var opts = new
        {
            id = note.Id,
            bg = $"#{bg.R:X2}{bg.G:X2}{bg.B:X2}",
            ink = dark ? "#f2f2f2" : "#1c1c1c",
            rule = dark ? "rgba(255,255,255,0.12)" : "rgba(0,0,0,0.08)",
            size = (int)Math.Round(_settings.Current.EditorFontSize),
            lines = _settings.Current.PaperLines,
            spell = _settings.Current.SpellCheck && !ContainsMyanmar(note.BodyPlain ?? ""),
        };
        var js = $"loadNote({JsonSerializer.Serialize(HtmlForNote(note))},{JsonSerializer.Serialize(opts)})";
        _ = NoteWeb.CoreWebView2.ExecuteScriptAsync(js);
        PushNotesToWeb();   // titles for [[wiki-link]] autocomplete

        if (_jumpTerm.Length > 0)   // opened from search → scroll to + highlight the match
        {
            var term = _jumpTerm;
            _jumpTerm = "";
            _ = NoteWeb.CoreWebView2.ExecuteScriptAsync(
                $"setTimeout(function(){{highlightTerm({JsonSerializer.Serialize(term)});}},120)");
        }
    }

    /// <summary>Send the note list (id + title) to the editor for [[ ]] autocomplete.
    /// The WebView keeps the list across note switches, so only re-push when it changed.</summary>
    private void PushNotesToWeb()
    {
        if (!_webReady || NoteWeb.CoreWebView2 is null || !_wikiDirty) return;
        _wikiDirty = false;
        var list = _notes.ListNotes()
            .Where(n => n.Type == NoteType.Note)
            .Select(n => new { id = n.Id, t = EffectiveTitle(n) })
            .Where(x => !string.IsNullOrWhiteSpace(x.t))
            .ToList();
        _ = NoteWeb.CoreWebView2.ExecuteScriptAsync($"setNotes({JsonSerializer.Serialize(list)})");
    }

    /// <summary>The body HTML to load: stored HTML as-is, or legacy/empty bodies rebuilt from plain text.</summary>
    private static string HtmlForNote(Note note)
    {
        var raw = note.BodyRtf ?? "";
        var t = raw.TrimStart();
        if (t.StartsWith("<")) return raw;                       // already our HTML
        return PlainToHtml(note.BodyPlain ?? "");                // legacy RTF / block-JSON / empty
    }

    private static string PlainToHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Legacy bodies separate paragraphs with \r (RichEditBox) or \n; treat both as breaks.
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var sb = new StringBuilder();
        foreach (var line in normalized.Split('\n'))
            sb.Append(line.Length == 0 ? "<div><br></div>" : $"<div>{WebUtility.HtmlEncode(line)}</div>");
        return sb.ToString();
    }

    private void SaveHtmlBody(string html, string text)
    {
        if (_loadingNote || _current is null) return;
        MaybeSnapshot(_current);     // capture the pre-edit state (throttled) before overwriting
        _current.BodyRtf = html;     // store HTML in the reused column
        _current.BodyPlain = text;   // plain text for FTS
        _notes.UpdateNote(_current);
        UpdateNodeTitle(_current);
    }

    /// <summary>Decode a pasted data-URL, save it (de-duped/downscaled/OCR'd), and insert it at the caret.</summary>
    private async Task HandleWebPasteImage(string dataUrl)
    {
        if (_current is null || string.IsNullOrEmpty(dataUrl)) return;
        var noteId = _current.Id;
        try
        {
            int comma = dataUrl.IndexOf(',');
            if (comma < 0) return;
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);

            var rel = await SavePastedImageBytesAsync(noteId, bytes);
            if (rel is null) return;

            var relWeb = rel.Replace('\\', '/');
            var url = "https://notes.local/" + relWeb;
            if (_current?.Id == noteId && NoteWeb.CoreWebView2 is not null)
                await NoteWeb.CoreWebView2.ExecuteScriptAsync(
                    $"insertImage({JsonSerializer.Serialize(url)},{JsonSerializer.Serialize(relWeb)})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("paste image failed: " + ex);
        }
    }

    // One shared client for pulling images referenced by pasted web content.
    private static readonly HttpClient _httpImages = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const long MaxPasteImageBytes = 25_000_000;

    /// <summary>
    /// An image referenced by pasted HTML (a remote http(s) URL or an inline data: URI).
    /// Download/decode it, save it as a local attachment, and rewrite the matching
    /// &lt;img&gt; in the editor to the local copy — so the note stays fully offline.
    /// </summary>
    private async Task HandleWebPasteRemoteImage(string src, string token)
    {
        if (_current is null || string.IsNullOrEmpty(src) || string.IsNullOrEmpty(token)) return;
        var noteId = _current.Id;
        try
        {
            byte[] bytes;
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = src.IndexOf(',');
                if (comma < 0) return;
                bytes = Convert.FromBase64String(src[(comma + 1)..]);
            }
            else if (src.StartsWith("http://") || src.StartsWith("https://"))
            {
                using var resp = await _httpImages.GetAsync(src, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return;
                var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (ct.Length > 0 && !ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return;
                if ((resp.Content.Headers.ContentLength ?? 0) > MaxPasteImageBytes) return;
                bytes = await resp.Content.ReadAsByteArrayAsync();
            }
            else return;

            if (bytes.Length == 0 || bytes.Length > MaxPasteImageBytes) return;

            var rel = await SavePastedImageBytesAsync(noteId, bytes);
            if (rel is null) return;

            var relWeb = rel.Replace('\\', '/');
            var url = "https://notes.local/" + relWeb;
            if (_current?.Id == noteId && NoteWeb.CoreWebView2 is not null)
                await NoteWeb.CoreWebView2.ExecuteScriptAsync(
                    $"localizeImage({JsonSerializer.Serialize(token)},{JsonSerializer.Serialize(url)},{JsonSerializer.Serialize(relWeb)})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("paste remote image failed: " + ex);
        }
    }

    // De-dupe pasted images within a session: content hash -> stored relative path.
    private readonly Dictionary<string, string> _pasteImageCache = new();
    private readonly SemaphoreSlim _ocrGate = new(1, 1);
    private const int MaxImageEdge = 2600;   // downscale anything larger to keep the folder lean

    /// <summary>
    /// Shared sink for every pasted image (screenshot, article image, data: URI). De-dupes by
    /// content hash, downscales oversized images, saves the attachment, and OCR-indexes it in the
    /// background so pasted images are searchable like screenshot threads. Returns the rel path.
    /// </summary>
    private async Task<string?> SavePastedImageBytesAsync(long noteId, byte[] bytes)
    {
        if (bytes.Length == 0) return null;

        var hash = noteId + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (_pasteImageCache.TryGetValue(hash, out var cachedRel)) return cachedRel;   // identical image already saved

        bytes = await MaybeDownscaleAsync(bytes);

        var rel = _paths.NewScreenshotRelPath(noteId);
        var abs = _paths.ToAbsolute(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        await File.WriteAllBytesAsync(abs, bytes);

        int w = 0, h = 0;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(abs);
            using var s = await file.OpenReadAsync();
            var dec = await BitmapDecoder.CreateAsync(s);
            w = (int)dec.PixelWidth; h = (int)dec.PixelHeight;
        }
        catch
        {
            // Not a decodable raster (e.g. SVG) — drop it rather than leave a broken file.
            try { File.Delete(abs); } catch { }
            return null;
        }

        var img = _notes.AddImage(noteId, rel, w, h);
        _pasteImageCache[hash] = rel;

        if (_ocr.IsAvailable)
            _ = OcrPastedImageAsync(img.Id, abs);   // background; serialized via _ocrGate

        return rel;
    }

    private async Task OcrPastedImageAsync(long imageId, string absPath)
    {
        await _ocrGate.WaitAsync();
        try
        {
            var text = await _ocr.RecognizeAsync(absPath);
            if (!string.IsNullOrWhiteSpace(text)) _notes.UpdateImageOcr(imageId, text);
        }
        catch { /* OCR is best-effort */ }
        finally { _ocrGate.Release(); }
    }

    /// <summary>Re-encode an image down to <see cref="MaxImageEdge"/> on its long edge (same codec). No-op when small or on error.</summary>
    private static async Task<byte[]> MaybeDownscaleAsync(byte[] bytes)
    {
        try
        {
            using var src = new InMemoryRandomAccessStream();
            var writer = new DataWriter(src);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            writer.DetachStream();
            src.Seek(0);

            var dec = await BitmapDecoder.CreateAsync(src);
            uint w = dec.PixelWidth, h = dec.PixelHeight, maxEdge = Math.Max(w, h);
            if (maxEdge <= MaxImageEdge) return bytes;

            double scale = (double)MaxImageEdge / maxEdge;
            using var outStream = new InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateForTranscodingAsync(outStream, dec);
            enc.BitmapTransform.ScaledWidth = (uint)Math.Round(w * scale);
            enc.BitmapTransform.ScaledHeight = (uint)Math.Round(h * scale);
            enc.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await enc.FlushAsync();

            outStream.Seek(0);
            var len = (uint)outStream.Size;
            var reader = new DataReader(outStream);
            await reader.LoadAsync(len);
            var outBytes = new byte[len];
            reader.ReadBytes(outBytes);
            return outBytes;
        }
        catch
        {
            return bytes;   // keep the original on any decode/encode failure
        }
    }

    /// <summary>Run a contenteditable execCommand (or helper) in the web editor.</summary>
    private void EditorExec(string cmd, string? val = null)
    {
        if (NoteWeb.CoreWebView2 is null) return;
        _ = NoteWeb.CoreWebView2.ExecuteScriptAsync(
            $"exec({JsonSerializer.Serialize(cmd)},{JsonSerializer.Serialize(val)})");
    }

    private sealed class WebMsg
    {
        public string Type { get; set; } = "";
        public long Id { get; set; }
        public string? Html { get; set; }
        public string? Text { get; set; }
        public string? Data { get; set; }
        public string? Src { get; set; }
    }

    // The editor document. Self-contained (HTML+CSS+JS) so there is no asset file to ship.
    private const string EditorHtml = """
<!DOCTYPE html><html><head><meta charset="utf-8">
<style>
 html,body{margin:0;padding:0;height:100%;background:transparent;}
 #ed{outline:none;min-height:100%;box-sizing:border-box;max-width:760px;margin:0 auto;
     padding:20px 28px 96px 28px;
     font-family:Calibri,'Segoe UI','Myanmar Text',sans-serif;font-size:16px;line-height:1.75;
     color:#1c1c1c;-webkit-user-select:text;word-wrap:break-word;}
 #ed>div{margin:0 0 .65em;}
 #ed p{margin:0 0 .65em;}
 #ed.lines{background-image:repeating-linear-gradient(to bottom,transparent 0,transparent calc(1.75em - 1px),
     var(--rule,rgba(0,0,0,.08)) calc(1.75em - 1px),var(--rule,rgba(0,0,0,.08)) 1.75em);
     background-position:0 20px;}
 #ed:empty:before{content:attr(data-ph);color:rgba(128,128,128,.65);}
 /* Images: no shadow/rounded box (that drew an ugly rectangle behind transparent PNGs).
    A picture can flow inline, wrap text left/right, or sit as its own block. */
 #ed img{max-width:100%;height:auto;display:block;margin:16px 0;cursor:default;transition:outline-color .12s ease;}
 #ed img.i-inline{display:inline-block;margin:0 4px;vertical-align:bottom;}
 #ed img.i-left{float:left;margin:5px 20px 12px 0;}
 #ed img.i-right{float:right;margin:5px 0 12px 20px;}
 #ed img.i-block{display:block;float:none;margin:16px auto;}
 #ed img:hover{outline:2px solid rgba(47,111,237,.35);outline-offset:2px;}
 #ed img.sel{outline:2px solid #2f6fed;outline-offset:2px;}
 /* Floating image toolbar — a clean pill with icon buttons, theme-aware via system colors. */
 #imgBar{position:fixed;z-index:10001;display:none;align-items:center;gap:1px;background:Canvas;color:CanvasText;
   border:1px solid rgba(128,128,128,.32);border-radius:11px;box-shadow:0 8px 28px rgba(0,0,0,.24);padding:4px;}
 #imgBar button{border:0;background:transparent;color:inherit;width:32px;height:30px;padding:0;cursor:pointer;
   border-radius:7px;display:inline-flex;align-items:center;justify-content:center;}
 #imgBar button:hover{background:rgba(128,128,128,.16);}
 #imgBar button.on{background:rgba(47,111,237,.15);color:#2f6fed;}
 #imgBar button svg{width:17px;height:17px;display:block;stroke:currentColor;stroke-width:1.7;fill:none;
   stroke-linecap:round;stroke-linejoin:round;}
 #imgBar .sep{width:1px;height:20px;background:rgba(128,128,128,.28);margin:0 4px;flex:none;}
 .imghdl{position:fixed;z-index:10000;display:none;width:12px;height:12px;border-radius:50%;background:#fff;
   border:2px solid #2f6fed;box-shadow:0 1px 4px rgba(0,0,0,.35);box-sizing:border-box;}
 #imgBadge{position:fixed;z-index:10002;display:none;background:#1f2430;color:#fff;font-size:12px;font-weight:600;
   padding:5px 9px;border-radius:7px;pointer-events:none;box-shadow:0 3px 12px rgba(0,0,0,.35);white-space:nowrap;}
 #imgMarker{position:fixed;z-index:10002;display:none;width:3px;border-radius:2px;background:#2f6fed;
   pointer-events:none;box-shadow:0 0 0 1px rgba(255,255,255,.65);}
 #ed ul,#ed ol{margin:.2em 0 .6em 1.4em;padding-left:1em;}
 #ed li{margin:.15em 0;}
 #ed h1{font-size:1.6em;font-weight:600;line-height:1.25;margin:.6em 0 .3em;}
 #ed h2{font-size:1.35em;font-weight:600;line-height:1.3;margin:.6em 0 .3em;}
 #ed h3{font-size:1.15em;font-weight:600;margin:.5em 0 .25em;}
 #ed h4{font-size:1.02em;font-weight:600;margin:.5em 0 .25em;}
 #ed h5,#ed h6{font-size:.95em;font-weight:600;opacity:.85;margin:.5em 0 .25em;}
 #ed blockquote{margin:.6em 0;padding:.15em 0 .15em 1em;border-left:3px solid rgba(128,128,128,.45);opacity:.92;}
 #ed pre{background:rgba(128,128,128,.12);padding:10px 12px;border-radius:6px;overflow:auto;
     font-family:Consolas,'Cascadia Mono',monospace;font-size:.92em;line-height:1.5;}
 #ed code{background:rgba(128,128,128,.14);padding:.05em .35em;border-radius:4px;
     font-family:Consolas,'Cascadia Mono',monospace;font-size:.92em;}
 #ed pre code{background:none;padding:0;}
 #ed figure{margin:.6em 0;}#ed figcaption{font-size:.85em;opacity:.65;margin-top:.25em;}
 ::-webkit-scrollbar{width:12px;}::-webkit-scrollbar-thumb{background:rgba(128,128,128,.4);border-radius:6px;}
 a{color:#3b82f6;}#ed a.wl{color:#3b82f6;text-decoration:none;border-bottom:1px solid rgba(59,130,246,.4);cursor:pointer;}
 #ed .src{margin:6px 0 10px;}#ed .src small{opacity:.65;}
 #wlpop{position:fixed;z-index:9999;background:Canvas;color:CanvasText;border:1px solid GrayText;border-radius:6px;
   box-shadow:0 6px 20px rgba(0,0,0,.28);min-width:200px;max-width:340px;overflow:hidden;font-size:14px;padding:4px 0;}
 .wlitem{padding:6px 12px;cursor:pointer;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
 .wlitem.sel{background:Highlight;color:HighlightText;}
 ::highlight(jump){background:#ffd54a;color:#1c1c1c;}
</style></head><body>
<div id="ed" contenteditable="true" data-ph="Start typing… (Ctrl+V to paste a screenshot)"></div>
<div id="imgBar">
  <button id="wInline" title="Inline with text"><svg viewBox="0 0 24 24"><line x1="4" y1="7" x2="20" y2="7"/><rect x="4" y="11" width="8" height="7" rx="1"/><line x1="15" y1="13" x2="20" y2="13"/><line x1="15" y1="17" x2="20" y2="17"/></svg></button>
  <button id="wLeft" title="Wrap text on the right"><svg viewBox="0 0 24 24"><rect x="4" y="6" width="9" height="9" rx="1"/><line x1="16" y1="8" x2="20" y2="8"/><line x1="16" y1="12" x2="20" y2="12"/><line x1="4" y1="19" x2="20" y2="19"/></svg></button>
  <button id="wRight" title="Wrap text on the left"><svg viewBox="0 0 24 24"><rect x="11" y="6" width="9" height="9" rx="1"/><line x1="4" y1="8" x2="8" y2="8"/><line x1="4" y1="12" x2="8" y2="12"/><line x1="4" y1="19" x2="20" y2="19"/></svg></button>
  <button id="wBlock" title="On its own line (centered)"><svg viewBox="0 0 24 24"><rect x="6" y="8" width="12" height="8" rx="1"/><line x1="4" y1="4" x2="20" y2="4"/><line x1="4" y1="20" x2="20" y2="20"/></svg></button>
  <span class="sep"></span>
  <button id="bFit" title="Fit to page width"><svg viewBox="0 0 24 24"><path d="M9 6 L5 6 L5 10"/><path d="M15 6 L19 6 L19 10"/><path d="M9 18 L5 18 L5 14"/><path d="M15 18 L19 18 L19 14"/></svg></button>
  <button id="bView" title="View full size"><svg viewBox="0 0 24 24"><circle cx="11" cy="11" r="6"/><line x1="15.5" y1="15.5" x2="20" y2="20"/></svg></button>
</div>
<div class="imghdl" data-c="nw"></div>
<div class="imghdl" data-c="ne"></div>
<div class="imghdl" data-c="sw"></div>
<div class="imghdl" data-c="se"></div>
<div id="imgBadge"></div>
<div id="imgMarker"></div>
<script>
 var ed=document.getElementById('ed');
 var W=window.chrome.webview;
 function post(m){W.postMessage(JSON.stringify(m));}
 var savedRange=null;
 function snap(){var s=getSelection();if(s.rangeCount&&ed.contains(s.anchorNode))savedRange=s.getRangeAt(0).cloneRange();}
 document.addEventListener('selectionchange',function(){if(document.activeElement===ed)snap();});
 var t=null,curId=0;
 function save(){clearTimeout(t);t=setTimeout(function(){post({type:'save',id:curId,html:ed.innerHTML,text:ed.innerText});},400);}
 ed.addEventListener('input',function(){clearJump();save();});
 // Tags kept when pasting rich web content; others are unwrapped (children kept) or
 // dropped (script/style/etc.) so a page's CSS/JS can't leak into or break the editor.
 var PASTE_OK={A:1,B:1,STRONG:1,I:1,EM:1,U:1,S:1,STRIKE:1,SUB:1,SUP:1,MARK:1,P:1,DIV:1,BR:1,
   SPAN:1,H1:1,H2:1,H3:1,H4:1,H5:1,H6:1,UL:1,OL:1,LI:1,BLOCKQUOTE:1,PRE:1,CODE:1,HR:1,IMG:1,
   FIGURE:1,FIGCAPTION:1,TABLE:1,THEAD:1,TBODY:1,TR:1,TD:1,TH:1};
 var PASTE_DROP={SCRIPT:1,STYLE:1,LINK:1,META:1,NOSCRIPT:1,HEAD:1,TITLE:1,SVG:1,IFRAME:1,
   OBJECT:1,EMBED:1,FORM:1,INPUT:1,BUTTON:1,SELECT:1,TEXTAREA:1,VIDEO:1,AUDIO:1};
 var imgSeq=0;
 var plainNext=false;   // set by Ctrl+Shift+V -> next paste is plain text
 // Strip common tracking params from pasted links so notes stay clean.
 var TRACK=/^(utm_.*|fbclid|gclid|gclsrc|dclid|msclkid|igshid|mc_eid|mc_cid|mkt_tok|_hsenc|_hsmi|hsa_.*|vero_id|vero_conv|oly_enc_id|oly_anon_id|piwik_.*|pk_.*|yclid|_openstat|ref|ref_src|spm|triedRedirect)$/i;
 function cleanUrl(u){try{var url=new URL(u);var del=[];url.searchParams.forEach(function(v,k){if(TRACK.test(k))del.push(k);});for(var i=0;i<del.length;i++)url.searchParams.delete(del[i]);return url.toString();}catch(e){return u;}}
 // True if a node contains block-level content (so we never let an <a> wrap blocks).
 function hasBlock(n){return !!(n.querySelector&&n.querySelector('p,div,h1,h2,h3,h4,h5,h6,ul,ol,li,blockquote,table,figure,hr,pre'));}
 function cleanInto(src,dst){
   for(var c=src.firstChild;c;c=c.nextSibling){
     if(c.nodeType===3){dst.appendChild(document.createTextNode(c.nodeValue));continue;}
     if(c.nodeType!==1)continue;
     var tag=c.tagName;
     if(PASTE_DROP[tag])continue;
     if(tag==='A'){
       // Keep only genuine INLINE links. Sites often wrap the whole article in one <a>,
       // which the browser flattens — turning every heading into same-size blue text.
       var h=c.getAttribute('href')||'';
       if(/^(https?:|mailto:)/i.test(h)&&!hasBlock(c)){
         var a=document.createElement('a');a.setAttribute('href',cleanUrl(h));a.setAttribute('target','_blank');
         cleanInto(c,a);dst.appendChild(a);
       } else { cleanInto(c,dst); }                        // unwrap block-wrapping / junk links
       continue;
     }
     if(!PASTE_OK[tag]){cleanInto(c,dst);continue;}        // unwrap unknown tag, keep its text
     var el=document.createElement(tag);
     if(tag==='IMG'){var s=c.getAttribute('src')||'';if(!/^(https?:|data:image\/)/i.test(s))continue;el.setAttribute('src',s);var al=c.getAttribute('alt');if(al)el.setAttribute('alt',al);}
     // Keep structural emphasis only — NOT source colors/sizes, so pasted text adopts the
     // editor's own (themed) typography instead of clashing blues and flat sizes.
     var st=c.getAttribute('style');
     if(st){var keep=st.match(/(font-weight|font-style|text-decoration|text-align)\s*:[^;]+/gi);if(keep)el.setAttribute('style',keep.join(';'));}
     cleanInto(c,el);
     dst.appendChild(el);
   }
 }
 // C# calls this after it has saved a pasted image locally: swap the remote/data src
 // for the local copy and tag it so it's clickable + persists offline.
 function localizeImage(tok,url,rel){
   var im=ed.querySelector('img[data-tok="'+tok+'"]');
   if(!im)return;
   im.setAttribute('src',url);im.setAttribute('data-rel',rel);im.removeAttribute('data-tok');save();
 }
 // Best title guess from current content: first real heading, else the first non-empty line.
 function autoTitle(){
   var h=ed.querySelector('h1,h2,h3,h4,h5,h6');
   var t=(h&&h.innerText)?h.innerText:'';
   if(!t.trim()){var kids=ed.children;for(var i=0;i<kids.length;i++){var k=(kids[i].innerText||'').trim();if(k){t=k;break;}}if(!t.trim())t=(ed.innerText||'');}
   return t.replace(/\s+/g,' ').trim().slice(0,120);
 }
 ed.addEventListener('paste',function(e){
   var cd=e.clipboardData||window.clipboardData;
   // Ctrl+Shift+V: force plain text, ignoring formatting and images.
   if(plainNext){plainNext=false;e.preventDefault();
     document.execCommand('insertText',false,cd?cd.getData('text'):'');post({type:'pasted',text:autoTitle()});return;}
   var types=(cd&&cd.types)||[];
   var hasHtml=Array.prototype.indexOf.call(types,'text/html')>=0;
   var items=(cd&&cd.items)||[];
   // A pure image copy (screenshot / "Copy image") with no rich HTML -> store as a screenshot.
   if(!hasHtml){
     for(var i=0;i<items.length;i++){
       if(items[i].type&&items[i].type.indexOf('image')===0){
         e.preventDefault();snap();
         var f=items[i].getAsFile();var r=new FileReader();
         r.onload=function(){post({type:'img',data:r.result});};r.readAsDataURL(f);return;
       }
     }
   }
   // Rich web content: keep the formatting + images, then localize every image.
   if(hasHtml){
     var html=cd.getData('text/html');
     if(html&&html.trim()){
       e.preventDefault();
       var tmp=document.createElement('div');tmp.innerHTML=html;
       var box=document.createElement('div');cleanInto(tmp,box);
       var imgs=box.querySelectorAll('img'),jobs=[];
       for(var j=0;j<imgs.length;j++){
         var src=imgs[j].getAttribute('src')||'';
         if(/^(https?:|data:image\/)/i.test(src)){var tok='pi'+(imgSeq++);imgs[j].setAttribute('data-tok',tok);jobs.push([tok,src]);}
       }
       document.execCommand('insertHTML',false,box.innerHTML);
       for(var k=0;k<jobs.length;k++)post({type:'pasteimg',src:jobs[k][1],data:jobs[k][0]});
       post({type:'pasted',text:autoTitle()});save();return;
     }
   }
   // Plain text fallback.
   e.preventDefault();
   var txt=cd?cd.getData('text'):'';
   document.execCommand('insertText',false,txt);
   post({type:'pasted',text:autoTitle()});
 });
 ed.addEventListener('click',function(e){
   var a=e.target&&e.target.closest?e.target.closest('a'):null;
   if(a){e.preventDefault();
     if(a.classList.contains('wl')){var id=parseInt(a.getAttribute('data-id'),10);if(id)post({type:'opennote',id:id});}
     else if(a.getAttribute('href')&&a.getAttribute('href')!=='#'){post({type:'openurl',src:a.getAttribute('href')});}
     return;}
   if(e.target&&e.target.tagName==='IMG'){selectImg(e.target);return;}
   deselectImg();   // clicked text/empty space -> drop any image selection
   var r=document.caretRangeFromPoint?document.caretRangeFromPoint(e.clientX,e.clientY):null;
   if(r&&r.startContainer.nodeType===3){
     var n=r.startContainer,v=n.nodeValue,i=r.startOffset;
     for(var k=Math.max(0,i-1);k<=i&&k<v.length;k++){
       if(v[k]==='☐'||v[k]==='☑'){n.nodeValue=v.substring(0,k)+(v[k]==='☐'?'☑':'☐')+v.substring(k+1);save();return;}
     }
   }
 });
 // ---- Image select / resize / wrap / move ------------------------------------------
 // Images stay in the document flow (so notes still reflow, search, and export cleanly).
 // Click one to select it: a floating toolbar sets how text wraps (inline / left / right /
 // own line), four corner handles resize it aspect-locked with a live size badge and soft
 // snapping, and dragging the picture body moves it to a new spot (a marker shows where).
 // Wrap is a CSS class and size is an inline width, so both live in the saved HTML.
 var imgBar=document.getElementById('imgBar'),imgBadge=document.getElementById('imgBadge'),
     imgMarker=document.getElementById('imgMarker'),selImg=null;
 var HDL={};
 [].forEach.call(document.querySelectorAll('.imghdl'),function(h){
   HDL[h.dataset.c]=h;
   h.style.cursor=(h.dataset.c==='nw'||h.dataset.c==='se')?'nwse-resize':'nesw-resize';
 });
 function colW(){var s=getComputedStyle(ed);return ed.clientWidth-parseFloat(s.paddingLeft)-parseFloat(s.paddingRight);}
 function wrapOf(img){var c=img.className;return /\bi-inline\b/.test(c)?'inline':/\bi-left\b/.test(c)?'left':/\bi-right\b/.test(c)?'right':'block';}
 function placeImgUi(){
   if(selImg&&!ed.contains(selImg)){selImg=null;}        // node went away on reload/delete
   if(!selImg){imgBar.style.display='none';for(var k in HDL)HDL[k].style.display='none';return;}
   var r=selImg.getBoundingClientRect();
   var pos={nw:[r.left,r.top],ne:[r.right,r.top],sw:[r.left,r.bottom],se:[r.right,r.bottom]};
   for(var c in HDL){HDL[c].style.display='block';HDL[c].style.left=(pos[c][0]-6)+'px';HDL[c].style.top=(pos[c][1]-6)+'px';}
   imgBar.style.display='flex';
   var bw=imgBar.offsetWidth||210, bh=imgBar.offsetHeight||38;
   var left=Math.min(Math.max(6,r.left),window.innerWidth-bw-6);
   var top=r.top-bh-10; if(top<6)top=Math.min(r.bottom+10,window.innerHeight-bh-6);
   imgBar.style.left=left+'px';imgBar.style.top=top+'px';
   var w=wrapOf(selImg);
   document.getElementById('wInline').classList.toggle('on',w==='inline');
   document.getElementById('wLeft').classList.toggle('on',w==='left');
   document.getElementById('wRight').classList.toggle('on',w==='right');
   document.getElementById('wBlock').classList.toggle('on',w==='block');
 }
 function selectImg(img){if(selImg&&selImg!==img)selImg.classList.remove('sel');selImg=img;img.classList.add('sel');placeImgUi();}
 function deselectImg(){if(selImg){selImg.classList.remove('sel');selImg=null;}imgBadge.style.display='none';placeImgUi();}
 function setWrap(mode){
   if(!selImg)return;
   selImg.classList.remove('i-inline','i-left','i-right','i-block');
   if(mode==='inline')selImg.classList.add('i-inline');
   else if(mode==='left')selImg.classList.add('i-left');
   else if(mode==='right')selImg.classList.add('i-right');
   else selImg.classList.add('i-block');
   placeImgUi();save();
 }
 document.getElementById('wInline').onclick=function(){setWrap('inline');};
 document.getElementById('wLeft').onclick=function(){setWrap('left');};
 document.getElementById('wRight').onclick=function(){setWrap('right');};
 document.getElementById('wBlock').onclick=function(){setWrap('block');};
 document.getElementById('bView').onclick=function(){if(selImg)post({type:'open',src:selImg.getAttribute('data-rel')});};
 function fitWidth(){if(!selImg)return;if(wrapOf(selImg)==='inline'||wrapOf(selImg)==='left'||wrapOf(selImg)==='right')setWrap('block');selImg.style.width='100%';selImg.style.height='';placeImgUi();save();}
 document.getElementById('bFit').onclick=fitWidth;

 // --- resize from any corner (aspect-locked, soft snap, live badge) ---
 var rz=null,raf=0;
 function beginResize(handle,e){
   if(!selImg)return; e.preventDefault(); e.stopPropagation();
   var r=selImg.getBoundingClientRect();
   rz={east:handle.dataset.c.indexOf('e')>=0,left:r.left,right:r.right};
   handle.setPointerCapture(e.pointerId);
   imgBadge.style.display='block';
 }
 function moveResize(e){
   if(!rz||!selImg)return;
   var max=colW(),min=60;
   var w=rz.east?(e.clientX-rz.left):(rz.right-e.clientX);
   w=Math.max(min,Math.min(w,max));
   var snapped=0;
   [.25,.5,.75,1].forEach(function(f){var t=Math.round(max*f);if(Math.abs(w-t)<14){w=t;snapped=f;}});
   if(raf)cancelAnimationFrame(raf);
   raf=requestAnimationFrame(function(){
     selImg.style.width=Math.round(w)+'px';selImg.style.height='';
     placeImgUi();
     imgBadge.style.display='block';
     imgBadge.textContent=Math.round(w/max*100)+'%'+(snapped?(snapped===1?' · full width':' · snap'):'')+'  ·  '+Math.round(w)+' px';
     var br=selImg.getBoundingClientRect();
     imgBadge.style.left=Math.max(6,Math.min(br.left,window.innerWidth-imgBadge.offsetWidth-6))+'px';
     imgBadge.style.top=Math.max(6,br.top-imgBadge.offsetHeight-8)+'px';
   });
 }
 function endResize(handle,e){if(rz){rz=null;try{handle.releasePointerCapture(e.pointerId);}catch(_){}imgBadge.style.display='none';placeImgUi();save();}}
 for(var c in HDL){(function(h){
   h.addEventListener('pointerdown',function(e){beginResize(h,e);});
   h.addEventListener('pointermove',function(e){moveResize(e);});
   h.addEventListener('pointerup',function(e){endResize(h,e);});
   h.addEventListener('dblclick',function(e){e.preventDefault();fitWidth();});   // double-click a handle = fit width
 })(HDL[c]);}

 // --- drag the picture body to move it within the text (marker shows the drop point) ---
 ed.addEventListener('dragstart',function(e){if(e.target&&e.target.tagName==='IMG')e.preventDefault();});  // suppress native image drag
 ed.addEventListener('dblclick',function(e){if(e.target&&e.target.tagName==='IMG'){e.preventDefault();post({type:'open',src:e.target.getAttribute('data-rel')});}});
 var mv=null;
 function caretFrom(x,y){
   var r=document.caretRangeFromPoint?document.caretRangeFromPoint(x,y):null;
   if(!r&&document.caretPositionFromPoint){var p=document.caretPositionFromPoint(x,y);if(p){r=document.createRange();r.setStart(p.offsetNode,p.offset);}}
   if(!r)return null;
   if(selImg&&selImg.contains(r.startContainer))return null;
   if(!ed.contains(r.startContainer))return null;
   r.collapse(true);return r;
 }
 ed.addEventListener('pointerdown',function(e){
   if(rz||!e.target||e.target.tagName!=='IMG')return;
   mv={img:e.target,x:e.clientX,y:e.clientY,pid:e.pointerId,on:false};
 });
 document.addEventListener('pointermove',function(e){
   if(!mv)return;
   if(!mv.on){
     if(Math.abs(e.clientX-mv.x)+Math.abs(e.clientY-mv.y)<6)return;
     mv.on=true; selImg=mv.img; mv.img.classList.remove('sel');
     imgBar.style.display='none';for(var k in HDL)HDL[k].style.display='none';
     mv.img.style.opacity='.55';document.body.style.cursor='grabbing';
   }
   var r=caretFrom(e.clientX,e.clientY); mv.range=r;
   if(r){var rc=r.getClientRects()[0]||r.getBoundingClientRect();
     imgMarker.style.display='block';imgMarker.style.left=rc.left+'px';
     imgMarker.style.top=rc.top+'px';imgMarker.style.height=(rc.height||20)+'px';}
   else imgMarker.style.display='none';
 });
 document.addEventListener('pointerup',function(){
   if(!mv)return;
   if(mv.on){
     mv.img.style.opacity='';document.body.style.cursor='';imgMarker.style.display='none';
     if(mv.range){try{mv.range.insertNode(mv.img);}catch(_){}}
     selectImg(mv.img);save();
   }
   mv=null;
 });

 ed.addEventListener('scroll',placeImgUi,true);
 window.addEventListener('resize',placeImgUi);
 ed.addEventListener('input',function(){if(selImg&&!ed.contains(selImg))deselectImg();});
 document.addEventListener('keydown',function(e){
   if(selImg&&(e.key==='Escape')){deselectImg();return;}
   if(selImg&&(e.key==='Delete'||e.key==='Backspace')&&document.activeElement!==ed){e.preventDefault();var g=selImg;deselectImg();g.parentNode&&g.parentNode.removeChild(g);save();}
 });
 document.addEventListener('keydown',function(e){
   if(wlPop){
     if(e.key==='ArrowDown'){e.preventDefault();wlSel=Math.min(wlSel+1,wlItems.length-1);paintWl();return;}
     if(e.key==='ArrowUp'){e.preventDefault();wlSel=Math.max(wlSel-1,0);paintWl();return;}
     if(e.key==='Enter'||e.key==='Tab'){e.preventDefault();chooseWl(wlSel);return;}
     if(e.key==='Escape'){e.preventDefault();closeWl();return;}
   }
   if((e.ctrlKey||e.metaKey)&&e.shiftKey&&(e.key==='v'||e.key==='V')){plainNext=true;setTimeout(function(){plainNext=false;},500);return;}
   if(e.ctrlKey&&(e.key==='f'||e.key==='F'||e.key==='k'||e.key==='K')){e.preventDefault();post({type:'focussearch'});}
   else if(e.ctrlKey&&(e.key==='h'||e.key==='H')){e.preventDefault();post({type:'find'});}
   else if(e.key==='F11'){e.preventDefault();post({type:'togglefocus'});}
   else if(e.key==='Escape'){post({type:'exitfocus'});}
 });
 // ---- [[wiki-link]] autocomplete ----
 var wikiNotes=[],wlPop=null,wlItems=[],wlSel=0,wlCtx=null;
 function setNotes(a){wikiNotes=a||[];}
 function closeWl(){if(wlPop){wlPop.remove();wlPop=null;}wlItems=[];wlCtx=null;}
 function paintWl(){if(!wlPop)return;var ch=wlPop.children;for(var i=0;i<ch.length;i++)ch[i].className='wlitem'+(i===wlSel?' sel':'');}
 function wlContext(){
   var s=getSelection();if(!s.rangeCount)return null;var r=s.getRangeAt(0);if(!r.collapsed)return null;
   var node=r.startContainer;if(node.nodeType!==3)return null;
   var text=node.nodeValue.substring(0,r.startOffset);var i=text.lastIndexOf('[[');if(i<0)return null;
   var q=text.substring(i+2);if(q.indexOf(']')>=0||q.indexOf('[')>=0||q.indexOf('\n')>=0)return null;
   return {node:node,start:i,end:r.startOffset,query:q};
 }
 function showWl(ctx){
   var q=ctx.query.toLowerCase();
   wlItems=wikiNotes.filter(function(n){return n.t.toLowerCase().indexOf(q)>=0;}).slice(0,8);
   if(wlItems.length===0){closeWl();return;}
   wlSel=0;wlCtx=ctx;
   if(!wlPop){wlPop=document.createElement('div');wlPop.id='wlpop';document.body.appendChild(wlPop);}
   wlPop.innerHTML='';
   wlItems.forEach(function(n,idx){var d=document.createElement('div');d.className='wlitem'+(idx===0?' sel':'');
     d.textContent=n.t;d.onmousedown=function(ev){ev.preventDefault();chooseWl(idx);};wlPop.appendChild(d);});
   var rect=getSelection().getRangeAt(0).getBoundingClientRect();
   wlPop.style.left=Math.max(8,rect.left)+'px';wlPop.style.top=(rect.bottom+4)+'px';
 }
 function chooseWl(idx){
   var n=wlItems[idx];if(!n||!wlCtx)return;var node=wlCtx.node;
   var before=node.nodeValue.substring(0,wlCtx.start),after=node.nodeValue.substring(wlCtx.end);
   var p=node.parentNode;
   var a=document.createElement('a');a.className='wl';a.setAttribute('data-id',n.id);a.href='#';a.textContent=n.t;
   var bn=document.createTextNode(before),an=document.createTextNode(' '+after);
   p.insertBefore(bn,node);p.insertBefore(a,node);p.insertBefore(an,node);p.removeChild(node);
   var r=document.createRange();r.setStart(an,1);r.collapse(true);var s=getSelection();s.removeAllRanges();s.addRange(r);
   closeWl();save();
 }
 ed.addEventListener('input',function(){var c=wlContext();if(c)showWl(c);else closeWl();});
 function appendSource(url){
   var div=document.createElement('div');div.className='src';
   var small=document.createElement('small');small.appendChild(document.createTextNode('Source: '));
   var a=document.createElement('a');a.href=url;a.textContent=url;small.appendChild(a);div.appendChild(small);
   var s=getSelection();
   if(s.rangeCount){var r=s.getRangeAt(0);r.collapse(false);r.insertNode(div);
     var nr=document.createRange();nr.setStartAfter(div);nr.collapse(true);s.removeAllRanges();s.addRange(nr);}
   else ed.appendChild(div);
   save();
 }
 function restore(){if(savedRange){var s=getSelection();s.removeAllRanges();s.addRange(savedRange);}}
 function loadNote(html,o){
   clearTimeout(t);curId=o.id||0;clearJump();
   selImg=null;placeImgUi();           // drop image selection from the previous note
   ed.innerHTML=html||'';
   ed.spellcheck=!!o.spell;
   document.documentElement.style.setProperty('--rule',o.rule);
   document.documentElement.style.background=o.bg;document.body.style.background=o.bg;
   ed.style.color=o.ink;ed.style.fontSize=o.size+'px';
   ed.classList.toggle('lines',!!o.lines);
   savedRange=null;setTimeout(function(){ed.focus();},0);
 }
 function insertImage(url,rel){
   ed.focus();restore();
   var img=document.createElement('img');img.src=url;img.setAttribute('data-rel',rel);
   var s=getSelection();
   if(s.rangeCount){
     var r=s.getRangeAt(0);r.deleteContents();r.insertNode(img);
     var p=document.createElement('div');p.appendChild(document.createElement('br'));
     img.parentNode.insertBefore(p,img.nextSibling);
     var nr=document.createRange();nr.setStart(p,0);nr.collapse(true);
     s.removeAllRanges();s.addRange(nr);savedRange=nr.cloneRange();
   }else{ed.appendChild(img);}
   save();
 }
 function clearJump(){ try{ if(window.CSS&&CSS.highlights) CSS.highlights.delete('jump'); }catch(e){} }
 function highlightTerm(term){
   clearJump();
   if(!term||!window.CSS||!CSS.highlights||typeof Highlight==='undefined') return;
   var lower=term.toLowerCase();
   var walker=document.createTreeWalker(ed,NodeFilter.SHOW_TEXT,null);
   var node,ranges=[],first=null;
   while(node=walker.nextNode()){
     var hay=node.nodeValue.toLowerCase(),idx=hay.indexOf(lower);
     while(idx>=0){
       var r=document.createRange();r.setStart(node,idx);r.setEnd(node,idx+term.length);
       ranges.push(r);if(!first)first=r;idx=hay.indexOf(lower,idx+term.length);
     }
   }
   if(ranges.length){
     CSS.highlights.set('jump',new Highlight(...ranges));
     var el=first.startContainer.parentElement;if(el&&el.scrollIntoView)el.scrollIntoView({block:'center'});
   }
 }
 function applyPaper(o){
   document.documentElement.style.setProperty('--rule',o.rule);
   document.documentElement.style.background=o.bg;document.body.style.background=o.bg;
   ed.style.color=o.ink;ed.classList.toggle('lines',!!o.lines);
 }
 function insertAudio(url){
   ed.focus();restore();
   var au=document.createElement('audio');au.controls=true;au.src=url;au.setAttribute('contenteditable','false');
   au.style.display='block';au.style.margin='10px 0';au.style.width='320px';
   var s=getSelection();
   if(s.rangeCount){var r=s.getRangeAt(0);r.deleteContents();r.insertNode(au);
     var p=document.createElement('div');p.appendChild(document.createElement('br'));au.parentNode.insertBefore(p,au.nextSibling);
     var nr=document.createRange();nr.setStart(p,0);nr.collapse(true);s.removeAllRanges();s.addRange(nr);savedRange=nr.cloneRange();}
   else ed.appendChild(au);
   save();
 }
 function exec(cmd,val){ed.focus();restore();document.execCommand(cmd,false,val||null);snap();save();}
 function setSpell(on){ed.spellcheck=!!on;}
 function findNext(q){if(!q)return;ed.focus();window.find&&window.find(q,false,false,true,false,true,false);}
 function replaceOne(q,r){var s=getSelection();if(s.rangeCount&&s.toString().toLowerCase()===String(q).toLowerCase()){document.execCommand('insertText',false,r);save();}findNext(q);}
 function replaceAll(q,r){if(!q)return 0;ed.focus();getSelection().collapse(ed,0);var n=0;
   while(window.find(q,false,false,true,false,true,false)){document.execCommand('insertText',false,r);n++;if(n>5000)break;}save();return n;}
 post({type:'ready'});
</script></body></html>
""";

    // ------------------------------------------------------------- Save paths
    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e) => SaveCurrentTitle((TextBox)sender);

    private void SaveCurrentTitle(TextBox box)
    {
        if (_loadingNote || _current is null) return;
        _wikiDirty = true;   // title changed → refresh [[ ]] autocomplete list
        _current.Title = box.Text;
        _notes.UpdateNote(_current);
        UpdateNodeTitle(_current);
    }

    /// <summary>After a paste into an untitled note, adopt the pasted heading / first line as the title.</summary>
    private void ApplyAutoTitle(string? candidate)
    {
        if (_current is null || _current.Type != NoteType.Note) return;
        candidate = candidate?.Trim();
        if (string.IsNullOrEmpty(candidate)) return;
        var cur = (_current.Title ?? "").Trim();
        if (cur.Length > 0 && cur != "New note") return;   // never overwrite a real, user-set title
        if (candidate.Length > 120) candidate = candidate[..120].TrimEnd();
        TitleBox.Text = candidate;   // TextChanged → SaveCurrentTitle persists it and refreshes the list
    }

    /// <summary>Effective title = explicit title, else the first non-empty body line.</summary>
    private static string EffectiveTitle(Note n)
    {
        if (!string.IsNullOrWhiteSpace(n.Title)) return n.Title.Trim();
        var line = (n.BodyPlain ?? "")
            .Split('\n', '\r')
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        return line?.Trim() ?? "";
    }

    private void UpdateNodeTitle(Note note)
    {
        var row = _noteRows.FirstOrDefault(r => r.Id == note.Id);
        if (row is not null)
        {
            var t = EffectiveTitle(note);
            row.Title = string.IsNullOrEmpty(t) ? "(untitled)" : t;
            row.Subtitle = NoteSubtitle(note);
            row.Pinned = note.Pinned && _filter != ListFilter.Trash;
        }
    }

    private static string FriendlyDateTime(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("dddd, MMMM d, yyyy  h:mm tt");

    private static string FriendlyAgo(long ms)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
        if (dt.Date == DateTime.Today) return "today at " + dt.ToString("h:mm tt");
        if (dt.Date == DateTime.Today.AddDays(-1)) return "yesterday at " + dt.ToString("h:mm tt");
        return dt.ToString("MMM d, yyyy 'at' h:mm tt");
    }

    /// <summary>
    /// RichEditBox bakes the default foreground into saved RTF; if the page is dark it
    /// saved white text, which is invisible on a light page later (and vice-versa).
    /// Swap the default black/white color-table entries to contrast the *page* color;
    /// deliberate colors (red/green/blue/orange) pass through untouched.
    /// </summary>
    private string NormalizeRtfForPaper(string rtf) => IsDark(PaperBackgroundColor())
        ? rtf.Replace(@"\red0\green0\blue0", @"\red255\green255\blue255")
        : rtf.Replace(@"\red255\green255\blue255", @"\red0\green0\blue0");

    private bool IsDarkMode() =>
        _settings.Current.Theme == AppTheme.Dark ||
        (_settings.Current.Theme == AppTheme.System &&
         Application.Current.RequestedTheme == ApplicationTheme.Dark);

    // WinUI caches accent ThemeResource brushes; flipping the root theme once forces them
    // to re-resolve against the new SystemAccentColor we just set.
    private void RefreshAccentLive()
    {
        if (Content is FrameworkElement fe)
        {
            var target = fe.RequestedTheme;
            fe.RequestedTheme = target == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            fe.RequestedTheme = target;
        }
    }

    /// <summary>
    /// Harmonize the whole UI with the active theme: a tonal hierarchy derived from the page
    /// color — the page is the brightest writing surface, the desk behind it a touch deeper,
    /// the chrome panels (sidebar, toolbar, title bar) deeper still. Only in light mode; a
    /// dark app theme or "Follow Windows" keeps the neutral surfaces (so text stays readable).
    /// </summary>
    private void ApplyThemeSurfaces()
    {
        if (string.IsNullOrEmpty(_settings.Current.AccentColor))
        {
            // Follow Windows: neutral cards floating on the Mica backdrop.
            var layer = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
            var alt = (Brush)Application.Current.Resources["LayerFillColorAltBrush"];
            RailCard.Background = layer;
            NoteListCard.Background = alt;          // a touch lighter so the list reads apart from the rail
            ToolbarBar.Background = alt;
            ContentArea.Background = null;          // gaps between cards show Mica
            ContentRoot.Background = null;
            AppTitleBar.Background = null;
            SidebarSplitter.Background = new SolidColorBrush(Colors.Transparent);
            RailSplitter.Background = new SolidColorBrush(Colors.Transparent);   // invisible resize grip
            return;
        }

        var accent = ParseHex(_settings.Current.AccentColor);
        double m = _settings.Current.ThemeIntensity;   // 0 = none, 1 = default, up to 2
        Windows.UI.Color desk, chrome, panel;          // rail = chrome, list = panel, editor desk = desk
        if (IsDarkMode())
        {
            // Dark themed palette: chrome is the darkest frame, panel/desk progressively lighter, and the
            // page (PaperBackgroundColor, ~0.18) is the brightest sheet — a proper dark hierarchy.
            chrome = MixColors(DarkBase, accent, 0.10 * m);
            panel = MixColors(DarkBase, accent, 0.115 * m);
            desk = MixColors(DarkBase, accent, 0.13 * m);
        }
        else
        {
            // Light mode: blend the page tint toward the accent so the chrome carries the hue
            // (darkening a near-white page just keeps it near-white — washed out).
            var page = PaperBackgroundColor();
            desk = MixColors(page, accent, 0.07 * m);
            panel = MixColors(page, accent, 0.11 * m);
            chrome = MixColors(page, accent, 0.15 * m);
        }
        RailCard.Background = new SolidColorBrush(chrome);
        NoteListCard.Background = new SolidColorBrush(panel);
        ToolbarBar.Background = new SolidColorBrush(chrome);
        AppTitleBar.Background = new SolidColorBrush(chrome);
        ContentArea.Background = new SolidColorBrush(desk);        // shared desk behind all floating cards
        ContentRoot.Background = null;                            // editor paper floats on the desk too
        RailSplitter.Background = new SolidColorBrush(Colors.Transparent);   // invisible resize grip
        SidebarSplitter.Background = new SolidColorBrush(Colors.Transparent);
    }

    // Linear blend from a to b by t (0..1).
    private static Windows.UI.Color MixColors(Windows.UI.Color a, Windows.UI.Color b, double t) =>
        Windows.UI.Color.FromArgb(255,
            (byte)(a.R * (1 - t) + b.R * t),
            (byte)(a.G * (1 - t) + b.G * t),
            (byte)(a.B * (1 - t) + b.B * t));

    private static readonly Windows.UI.Color DarkBase = Windows.UI.Color.FromArgb(255, 28, 28, 28);

    private Windows.UI.Color PaperBackgroundColor()
    {
        if (IsDarkMode())
        {
            // A theme's curated page tint is light and would clash in dark mode, so derive a
            // DARK page carrying the accent hue. Without a theme, respect an explicit page
            // color if the user set one, else a neutral dark page.
            var ac = _settings.Current.AccentColor;
            if (!string.IsNullOrEmpty(ac)) return MixColors(DarkBase, ParseHex(ac), 0.18 * _settings.Current.ThemeIntensity);
            var ph = _settings.Current.PageColor;
            return string.IsNullOrEmpty(ph) ? Windows.UI.Color.FromArgb(255, 39, 39, 39) : ParseHex(ph);
        }
        var hex = _settings.Current.PageColor;
        return string.IsNullOrEmpty(hex) ? Colors.White : ParseHex(hex);
    }

    private static Windows.UI.Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Windows.UI.Color.FromArgb(255,
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    private static bool IsDark(Windows.UI.Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 < 0.5;

    /// <summary>Apply page color, text contrast, and rule-line pattern to the open note.</summary>
    private void ApplyPaperStyle()
    {
        var bg = PaperBackgroundColor();
        PaperBorder.Background = new SolidColorBrush(bg);

        var fg = new SolidColorBrush(IsDark(bg) ? Colors.White : Windows.UI.Color.FromArgb(255, 28, 28, 28));
        TitleBox.Foreground = fg;
        Editor.Foreground = fg;
        DateLine.Foreground = fg;
        EditedLine.Foreground = fg;
        RedrawPaper();
        PushPaperToWeb();   // update the open WebView note's background/ink without reloading
    }

    private void PushPaperToWeb()
    {
        if (!_webReady || NoteWeb.CoreWebView2 is null) return;
        var bg = PaperBackgroundColor();
        bool dark = IsDark(bg);
        var o = new
        {
            bg = $"#{bg.R:X2}{bg.G:X2}{bg.B:X2}",
            ink = dark ? "#f2f2f2" : "#1c1c1c",
            rule = dark ? "rgba(255,255,255,0.12)" : "rgba(0,0,0,0.08)",
            lines = _settings.Current.PaperLines,
        };
        _ = NoteWeb.CoreWebView2.ExecuteScriptAsync($"applyPaper({JsonSerializer.Serialize(o)})");
    }

    private void PaperCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawPaper();

    // The RichEditBox's inner scroller, so rule lines can scroll with the text.
    private ScrollViewer? _editorScroll;
    private int _scrollHookTries;

    private void EnsureEditorScrollHooked()
    {
        if (_editorScroll is not null) return;
        _editorScroll = FindDescendant<ScrollViewer>(Editor);
        if (_editorScroll is not null)
        {
            _editorScroll.ViewChanged += (_, _) => RedrawPaper();
            RedrawPaper();
            return;
        }
        if (_scrollHookTries++ < 12) DispatcherQueue.TryEnqueue(EnsureEditorScrollHooked);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : class
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    /// <summary>
    /// Measure the editor's first-line top and the true line-to-line advance (content
    /// space). Advance (line1.top → line2.top) is used rather than caret height so the
    /// rules don't drift out of phase with the text over many lines. No line spacing is
    /// forced, so tall scripts (e.g. Myanmar stacked glyphs) are never clipped.
    /// </summary>
    private (double top, double height) MeasureTextLine()
    {
        double off = _editorScroll?.VerticalOffset ?? 0;
        double fallback = Math.Max(18, Editor.FontSize * 1.35);
        try
        {
            var doc = Editor.Document;
            var r0 = doc.GetRange(0, 0);
            r0.GetRect(PointOptions.ClientCoordinates, out var rect0, out _);
            double top = rect0.Top + off;

            var r1 = doc.GetRange(0, 0);
            if (r1.MoveStart(TextRangeUnit.Line, 1) != 0)   // jump to the 2nd line
            {
                r1.SetRange(r1.StartPosition, r1.StartPosition);
                r1.GetRect(PointOptions.ClientCoordinates, out var rect1, out _);
                double advance = rect1.Top - rect0.Top;
                if (advance >= 8) return (top, advance);
            }
            return (top, rect0.Height >= 8 ? rect0.Height : fallback);
        }
        catch { return (14, fallback); }
    }

    private void RedrawPaper()
    {
        PaperCanvas.Children.Clear();
        if (!_settings.Current.PaperLines) return;
        double w = PaperCanvas.ActualWidth, h = PaperCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        bool dark = IsDark(PaperBackgroundColor());
        var brush = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(70, 255, 255, 255)
            : Windows.UI.Color.FromArgb(64, 0, 0, 0));
        var type = _settings.Current.PaperType;
        const double left = 72, right = 72;
        static double Snap(double v) => Math.Floor(v) + 0.5;

        // Text-aware: spacing = the editor's real line height; lines sit under each
        // text row and scroll with the content.
        var (top, lineH) = MeasureTextLine();
        double off = _editorScroll?.VerticalOffset ?? 0;

        double firstK = Math.Max(1, Math.Ceiling((off - top) / lineH));
        for (double k = firstK; ; k++)
        {
            double cy = Snap(top + lineH * k - off);   // bottom of row k → text rests on it
            if (cy > h) break;
            if (cy < 1) continue;
            var ln = new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = left, Y1 = cy, X2 = w - right, Y2 = cy, StrokeThickness = 1, Stroke = brush,
            };
            if (type == PaperPattern.Dotted)
            {
                ln.StrokeThickness = 1.8;
                ln.StrokeDashArray = new DoubleCollection { 0.01, 3.5 };
                ln.StrokeDashCap = PenLineCap.Round;
            }
            PaperCanvas.Children.Add(ln);
        }

        if (type == PaperPattern.Grid)
            for (double x = left; x < w - right; x += lineH)
            {
                double xx = Snap(x);
                PaperCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
                { X1 = xx, Y1 = 0, X2 = xx, Y2 = h, StrokeThickness = 1, Stroke = brush });
            }

        if (type == PaperPattern.Notebook)
            PaperCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = Snap(left - 18), Y1 = 0, X2 = Snap(left - 18), Y2 = h, StrokeThickness = 1.5,
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 214, 90, 90)),
            });
    }

    // ----------------------------------------- Editor formatting (drives WebView2)
    private void Bold_Click(object s, RoutedEventArgs e)      => EditorExec("bold");
    private void Italic_Click(object s, RoutedEventArgs e)    => EditorExec("italic");
    private void Underline_Click(object s, RoutedEventArgs e) => EditorExec("underline");
    private void Strike_Click(object s, RoutedEventArgs e)    => EditorExec("strikeThrough");

    private void FontBigger_Click(object s, RoutedEventArgs e)  => BumpEditorFont(+1);
    private void FontSmaller_Click(object s, RoutedEventArgs e) => BumpEditorFont(-1);
    private void BumpEditorFont(int delta)
    {
        var size = Math.Clamp(_settings.Current.EditorFontSize + delta, 10, 40);
        _settings.Current.EditorFontSize = size;
        _settings.Save();
        if (NoteWeb.CoreWebView2 is not null)
            _ = NoteWeb.CoreWebView2.ExecuteScriptAsync($"ed.style.fontSize='{(int)size}px';ed.focus();");
    }

    private void StyleTitle_Click(object s, RoutedEventArgs e)   => EditorExec("formatBlock", "H1");
    private void StyleHeading_Click(object s, RoutedEventArgs e) => EditorExec("formatBlock", "H2");
    private void StyleBody_Click(object s, RoutedEventArgs e)    => EditorExec("formatBlock", "P");

    private void Bullets_Click(object s, RoutedEventArgs e)  => EditorExec("insertUnorderedList");
    private void Numbered_Click(object s, RoutedEventArgs e) => EditorExec("insertOrderedList");
    private void Checklist_Click(object s, RoutedEventArgs e) => EditorExec("insertText", "☐ ");

    private void Highlight_Click(object s, RoutedEventArgs e) => EditorExec("hiliteColor", "#fff2a8");

    private void ColorDefault_Click(object s, RoutedEventArgs e) => EditorExec("foreColor", "inherit");
    private void ColorRed_Click(object s, RoutedEventArgs e)     => EditorExec("foreColor", "#d13438");
    private void ColorGreen_Click(object s, RoutedEventArgs e)   => EditorExec("foreColor", "#107c10");
    private void ColorBlue_Click(object s, RoutedEventArgs e)    => EditorExec("foreColor", "#0078d4");
    private void ColorOrange_Click(object s, RoutedEventArgs e)  => EditorExec("foreColor", "#ca5010");

    private void Font_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string font })
            EditorExec("fontName", font);
    }

    // The hidden RichEditBox (retained for compile-compat) never receives taps.
    private void Editor_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) { }

    // ---------------------------------------------------- Thread image actions
    private void ThreadImage_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThreadCard c)
            OpenImageViewer(c.Path, () => DeleteThreadImageAsync(c));
    }

    private void ThreadImageView_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThreadCard c)
            OpenImageViewer(c.Path, () => DeleteThreadImageAsync(c));
    }

    private async void ThreadImageCopy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ThreadCard c) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(c.Path);
            var dp = new DataPackage();
            dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            Clipboard.SetContent(dp);
        }
        catch { }
    }

    private async void ThreadImageExport_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThreadCard c) await ExportImageAsync(c.Path);
    }

    private async void ThreadImageDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThreadCard c) await DeleteThreadImageAsync(c);
    }

    /// <summary>Persist the card order after a drag-reorder in the thread.</summary>
    private void ThreadCards_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_current is null) return;
        _notes.ReorderImages(_current.Id, _cards.Select(c => c.Id).ToList());
        RenumberCards();
    }

    private async Task DeleteThreadImageAsync(ThreadCard c)
    {
        if (_current is null) return;
        if (_settings.Current.ConfirmBeforeDelete)
        {
            var d = new ContentDialog
            {
                Title = "Delete image?",
                Content = "Remove this image from the thread? This can't be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        }
        var rel = _notes.DeleteImage(c.Id);
        try { var abs = _paths.ToAbsolute(rel); if (File.Exists(abs)) File.Delete(abs); } catch { }
        LoadCards(_current.Id);
    }

    private async Task ExportImageAsync(string absPath)
    {
        if (string.IsNullOrEmpty(absPath) || !File.Exists(absPath)) return;
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(absPath);
        InitializeWithWindow(picker);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try { File.Copy(absPath, file.Path, overwrite: true); }
        catch (Exception ex) { await ShowInfoAsync("Export failed", ex.Message); }
    }

    private async void ThreadExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var imgs = _notes.ListImages(_current.Id);
        if (imgs.Count == 0) { await ShowInfoAsync("Export all images", "This thread has no images yet."); return; }

        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        int n = 0, i = 1;
        foreach (var img in imgs)
        {
            var abs = _paths.ToAbsolute(img.RelPath);
            if (File.Exists(abs))
            {
                try
                {
                    var name = $"{i:D3}_{Path.GetFileName(abs)}";
                    File.Copy(abs, Path.Combine(folder.Path, name), overwrite: false);
                    n++;
                }
                catch { }
            }
            i++;
        }
        await ShowInfoAsync("Export all images", $"Exported {n} image(s) to:\n{folder.Path}");
    }

    private void OpenImageViewer(string path, Func<Task>? onDelete = null)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        new ImageViewerWindow(path, onDelete).Activate();
    }

    // ---------------------------------------------------------------- Backup
    private void Editor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingNote || _current is null) return;
        Editor.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
        Editor.Document.GetText(TextGetOptions.None, out var plain);
        _current.BodyRtf = rtf;
        _current.BodyPlain = plain;
        _notes.UpdateNote(_current);
        ApplySpellCheck(plain);   // disable squiggles once Myanmar text appears
        UpdateNodeTitle(_current); // first-line-as-title fallback keeps the sidebar in sync
        if (_settings.Current.PaperLines) RedrawPaper();   // keep rules aligned as text grows
    }

    /// <summary>
    /// Windows has no Myanmar (Burmese) spell-check dictionary, so red squiggles on
    /// Myanmar text are just noise. Turn spell-check off whenever the note contains
    /// Myanmar script; otherwise honor the user's setting.
    /// </summary>
    private void ApplySpellCheck(string plainText)
    {
        var desired = _settings.Current.SpellCheck && !ContainsMyanmar(plainText);
        if (Editor.IsSpellCheckEnabled != desired) Editor.IsSpellCheckEnabled = desired;
    }

    private static bool ContainsMyanmar(string s)
    {
        foreach (var ch in s)
        {
            // Myanmar U+1000-U+109F, Extended-A U+AA60-U+AA7F, Extended-B U+A9E0-U+A9FF.
            if ((ch >= 'က' && ch <= '႟') ||
                (ch >= 'ꩠ' && ch <= 'ꩿ') ||
                (ch >= 'ꧠ' && ch <= '꧿'))
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------- New items
    private void NewNote_Click(object sender, RoutedEventArgs e) => CreateAndOpen(NoteType.Note, "New note");
    private void NewThread_Click(object sender, RoutedEventArgs e) => CreateAndOpen(NoteType.Thread, "New thread");

    private void CreateAndOpen(NoteType type, string title)
    {
        // Create in the current folder when one is selected, otherwise unfiled.
        long? folder = _filter == ListFilter.Folder ? _filterId : (long?)null;
        var note = _notes.CreateNote(title, type, folder);

        // Make sure the new note is visible in the list; if the active filter wouldn't show
        // it (e.g. a smart folder or trash), fall back to All Notes.
        bool visible = _filter == ListFilter.AllNotes
                       || (_filter == ListFilter.Folder && folder == _filterId)
                       || (_filter == ListFilter.Unfiled && folder is null);
        if (!visible) { _filter = ListFilter.AllNotes; _filterId = 0; _filterTitle = "All Notes"; _filterQuery = ""; HighlightCurrentFilterNode(); }
        PopulateNoteList();
        ShowNote(note.Id);
    }

    // ------------------------------------------------------------- Paste flow
    private async void PasteImageIntoThread()
    {
        if (_current is null || _current.Type != NoteType.Thread) return;

        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap)) return;

        var rel = _paths.NewScreenshotRelPath(_current.Id);
        var abs = _paths.ToAbsolute(rel);
        var (w, h) = await SaveClipboardBitmapToPngAsync(content, abs);

        var img = _notes.AddImage(_current.Id, rel, w, h);
        _cards.Add(ThreadCard.From(img, abs));
        RenumberCards();

        if (_ocr.IsAvailable)
        {
            var text = await _ocr.RecognizeAsync(abs);
            if (!string.IsNullOrWhiteSpace(text)) _notes.UpdateImageOcr(img.Id, text);
        }
    }

    private static async Task<(int w, int h)> SaveClipboardBitmapToPngAsync(DataPackageView content, string absPath)
    {
        var streamRef = await content.GetBitmapAsync();
        using var src = await streamRef.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(src);
        var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(absPath)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(absPath), CreationCollisionOption.ReplaceExisting);
        using var outStream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
        bitmap.Dispose();
        return (w, h);
    }

    // ---------------------------------------------------------------- Search
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        var q = (sender.Text ?? "").Trim();
        if (q.Length == 0)
        {
            _searchTimer?.Stop();
            // Leave the results view; go back to the open note (or the empty state).
            SearchResultsPane.Visibility = Visibility.Collapsed;
            if (_current is not null) ShowNote(_current.Id);
            else { HideDetailPanes(); EmptyState.Visibility = Visibility.Visible; }
            return;
        }
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        // Debounce: only run the query/render once typing pauses ~150 ms.
        _pendingSearch = q;
        _searchTimer ??= CreateSearchTimer();
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateSearchTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { if (_pendingSearch.Length > 0) ShowSearchResults(_pendingSearch); };
        return timer;
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var q = (args.QueryText ?? "").Trim();
        if (q.Length == 0) return;
        var hits = _notes.Search(q, 1);            // Enter opens the top hit
        if (hits.Count > 0) OpenSearchHit(hits[0].NoteId, q);
        else ShowSearchResults(q);
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) { }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down && SearchResultsPane.Visibility == Visibility.Visible && SearchResultsList.Items.Count > 0)
        {
            SearchResultsList.SelectedIndex = 0;
            (SearchResultsList.ContainerFromIndex(0) as ListViewItem)?.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    /// <summary>Show ranked results as a rich list in the content pane.</summary>
    // ================================================================ Timeline
    // A notebook-wide, date-grouped view of every note. Rows are bucketed into
    // natural sections (Today, Yesterday, this week/month, then by month) and
    // wrapped in a SemanticZoom so the user can zoom out to jump between periods.

    private TimelineAxis _timelineAxis = TimelineAxis.Modified;

    private void Timeline_Click(object sender, RoutedEventArgs e) => ShowTimeline();

    private void ShowTimeline()
    {
        StopTts();
        HideDetailPanes();
        TimelinePane.Visibility = Visibility.Visible;
        SidebarTree.SelectedNode = null;   // a global view, not a single note
        RebuildTimeline();
    }

    private void TimelineAxis_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TimelineAxisCombo.SelectedItem is ComboBoxItem { Tag: string tag })
            _timelineAxis = tag == "Created" ? TimelineAxis.Created : TimelineAxis.Modified;
        if (TimelinePane.Visibility == Visibility.Visible) RebuildTimeline();
    }

    private void RebuildTimeline()
    {
        var now = DateTimeOffset.Now.LocalDateTime;
        var groups = new List<TimelineGroup>();
        TimelineGroup? cur = null;

        // ListTimeline is already ordered newest-first by the chosen axis, so we can
        // build groups in a single pass and they come out in the right order.
        foreach (var n in _notes.ListTimeline(_timelineAxis))
        {
            var ts = _timelineAxis == TimelineAxis.Created ? n.CreatedAt : n.UpdatedAt;
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime;
            var bucket = TimelineBucket(dt, now);
            if (cur is null || cur.Key != bucket)
            {
                cur = new TimelineGroup { Key = bucket };
                groups.Add(cur);
            }

            var title = EffectiveTitle(n);
            var preview = FirstLine(n.BodyPlain);
            if (string.Equals(preview, title, StringComparison.Ordinal)) preview = "";  // don't echo the title
            cur.Add(new TimelineRow
            {
                NoteId = n.Id,
                Type = n.Type,
                Title = string.IsNullOrEmpty(title) ? "(untitled)" : title,
                Preview = preview,
                TimeText = dt.ToString("MMM d · h:mm tt"),
            });
        }

        TimelineCvs.Source = groups;
        TimelineList.ItemsSource = TimelineCvs.View;
        TimelineJumpList.ItemsSource = TimelineCvs.View.CollectionGroups;
    }

    private void TimelineItem_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TimelineRow row) ShowNote(row.NoteId);
    }

    /// <summary>Natural date-bucket label for the timeline, relative to now (local time).</summary>
    private static string TimelineBucket(DateTime dt, DateTime now)
    {
        var today = now.Date;
        var day = dt.Date;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        if (day > today.AddDays(-7)) return "Earlier this week";
        if (day.Year == today.Year && day.Month == today.Month) return "Earlier this month";
        return dt.ToString(day.Year == today.Year ? "MMMM" : "MMMM yyyy");
    }

    private static string FirstLine(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return "";
        var line = plain.Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        return line?.Trim() ?? "";
    }

    private void ShowSearchResults(string query)
    {
        var hits = _notes.Search(query, limit: 100);
        HideDetailPanes();
        SearchResultsPane.Visibility = Visibility.Visible;
        SearchResultsHeader.Text = hits.Count == 1 ? "1 result" : $"{hits.Count} results";
        SearchResultsList.Items.Clear();
        foreach (var h in hits)
            SearchResultsList.Items.Add(BuildResultItem(h, query));
    }

    private ListViewItem BuildResultItem(SearchHit h, string query)
    {
        var panel = new StackPanel { Spacing = 3, Padding = new Thickness(0, 4, 0, 4) };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(new FontIcon
        {
            Glyph = h.Type == NoteType.Thread ? "" : "",   // pictures vs document
            FontSize = 14,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        });
        top.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(h.Title) ? "(untitled)" : h.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(top);

        if (!string.IsNullOrWhiteSpace(h.Preview))
            panel.Children.Add(HighlightedSnippet(h.Preview));

        var date = DateTimeOffset.FromUnixTimeMilliseconds(h.Updated).LocalDateTime.ToString("MMM d, yyyy");
        panel.Children.Add(new TextBlock { Text = $"{h.Folder} · {date}", FontSize = 12, Opacity = 0.55 });

        var item = new ListViewItem { Content = panel, Tag = new SearchOpen(h.NoteId, query) };
        // ItemClick doesn't fire for directly-added ListViewItem containers; tap on the item.
        item.Tapped += (_, _) => OpenSearchHit(h.NoteId, query);
        return item;
    }

    /// <summary>Build a snippet TextBlock with the FTS match sentinels rendered as accent runs.</summary>
    private static TextBlock HighlightedSnippet(string s)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var accent = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        int i = 0;
        while (i < s.Length)
        {
            int open = s.IndexOf('', i);
            if (open < 0) { tb.Inlines.Add(new Run { Text = s[i..] }); break; }
            if (open > i) tb.Inlines.Add(new Run { Text = s[i..open] });
            int close = s.IndexOf('', open + 1);
            if (close < 0) close = s.Length;
            tb.Inlines.Add(new Run { Text = s[(open + 1)..close], FontWeight = FontWeights.SemiBold, Foreground = accent });
            i = Math.Min(close + 1, s.Length);
        }
        return tb;
    }

    private void SearchResult_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ListViewItem { Tag: SearchOpen o }) OpenSearchHit(o.NoteId, o.Query);
    }

    private void SearchResultsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && SearchResultsList.SelectedItem is ListViewItem { Tag: SearchOpen o })
        {
            OpenSearchHit(o.NoteId, o.Query);
            e.Handled = true;
        }
    }

    private void OpenSearchHit(long noteId, string query)
    {
        _jumpTerm = FirstTerm(query);   // scroll-to/highlight after the note loads
        ShowNote(noteId);
    }

    /// <summary>First plain search term (ignoring -, field:, OR, quotes) for jump-to-match.</summary>
    private static string FirstTerm(string q)
    {
        foreach (var tok in q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = tok.Trim('"');
            if (t.Length < 2 || t.StartsWith('-') || t.Contains(':') || t == "OR") continue;
            return t;
        }
        return "";
    }

    private readonly record struct SearchOpen(long NoteId, string Query);

    // ----------------------------------------------------------- Accelerators
    private void RegisterAccelerators()
    {
        if (Content is not UIElement root) return;
        // Don't render the floating "Ctrl+N" hint badges over the canvas.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Add(root, VirtualKey.N, () => NewNote_Click(this, new RoutedEventArgs()));
        Add(root, VirtualKey.T, () => NewThread_Click(this, new RoutedEventArgs()));

        // Ctrl+V for the screenshot-thread pane only. ScopeOwner keeps it from firing
        // (and swallowing the keystroke) when the WebView2 note editor has focus — the
        // web editor handles its own paste internally.
        // App-wide so it works no matter where focus is — OnPasteAccelerator only acts when a
        // screenshot thread is open AND the clipboard holds an image, so it never disturbs text
        // paste (search box, titles) or the WebView2 note editor's own paste.
        var paste = new KeyboardAccelerator { Key = VirtualKey.V, Modifiers = VirtualKeyModifiers.Control };
        paste.Invoked += (_, e) => OnPasteAccelerator(e);
        root.KeyboardAccelerators.Add(paste);

        // Ctrl+F (and Ctrl+K): jump to the global search box — the universal shortcut.
        Add(root, VirtualKey.F, FocusSearch);
        Add(root, VirtualKey.K, FocusSearch);
        // Ctrl+H: find & replace within the current note.
        Add(root, VirtualKey.H, ToggleFindBar);

        // F11 toggles focus mode; Esc leaves it (for when focus is outside the WebView).
        var f11 = new KeyboardAccelerator { Key = VirtualKey.F11 };
        f11.Invoked += (_, e) => { SetFocusMode(!_focusMode); e.Handled = true; };
        root.KeyboardAccelerators.Add(f11);
        var esc = new KeyboardAccelerator { Key = VirtualKey.Escape };
        esc.Invoked += (_, e) => { if (_focusMode) { SetFocusMode(false); e.Handled = true; } };
        root.KeyboardAccelerators.Add(esc);

        static void Add(UIElement el, VirtualKey key, Action action)
        {
            var acc = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            acc.Invoked += (_, e) => { action(); e.Handled = true; };
            el.KeyboardAccelerators.Add(acc);
        }
    }

    private void OnPasteAccelerator(KeyboardAcceleratorInvokedEventArgs e)
    {
        if (_current is null) return;
        // Notes are edited in the WebView2, which handles its own paste (text + images)
        // internally — leave it alone. Only the screenshot-thread pane needs us to step in.
        if (_current.Type != NoteType.Thread) return;
        if (!Clipboard.GetContent().Contains(StandardDataFormats.Bitmap)) return;
        PasteImageIntoThread();
        e.Handled = true;
    }

    private void Editor_Paste(object sender, TextControlPasteEventArgs e)
    {
        // Let RichEditBox paste the image natively (exactly one copy — e.Handled does NOT
        // cancel its image paste, so we must not insert our own as well). Separately we
        // archive a full-resolution copy on disk so double-click can open it at 100%.
        if (_current is not null && Clipboard.GetContent().Contains(StandardDataFormats.Bitmap))
            _ = ArchivePastedImage();
    }

    private async Task ArchivePastedImage()
    {
        if (_current is null) return;
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap)) return;
        try
        {
            var streamRef = await content.GetBitmapAsync();
            using var src = await streamRef.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(src);
            int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;
            var full = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var rel = _paths.NewScreenshotRelPath(_current.Id);
            var abs = _paths.ToAbsolute(rel);
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(abs)!);
            var file = await folder.CreateFileAsync(Path.GetFileName(abs), CreationCollisionOption.ReplaceExisting);
            using (var fs = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var fe = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fs);
                fe.SetSoftwareBitmap(full);
                await fe.FlushAsync();
            }
            full.Dispose();
            _notes.AddImage(_current.Id, rel, w, h);   // maps Nth embedded image -> Nth archived file
        }
        catch { /* ignore unsupported clipboard payloads */ }
    }

    /// <summary>Double-click an embedded image -> open the saved full-res file at 100%.</summary>
    private void Editor_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (_current is null) return;
        var p = e.GetPosition(Editor);
        var range = Editor.Document.GetRangeFromPoint(new Windows.Foundation.Point(p.X, p.Y), PointOptions.ClientCoordinates);
        if (range is null) return;
        range.Expand(TextRangeUnit.Character);
        range.GetText(TextGetOptions.None, out var ch);
        if (ch != "￼") return;   // not on an embedded object

        // Image index = number of embedded objects up to and including this position.
        Editor.Document.GetText(TextGetOptions.None, out var all);
        int pos = Math.Min(range.StartPosition, all.Length - 1);
        int index = -1;
        for (int i = 0; i <= pos && i < all.Length; i++)
            if (all[i] == '￼') index++;

        var imgs = _notes.ListImages(_current.Id);
        if (index >= 0 && index < imgs.Count)
            OpenImageViewer(_paths.ToAbsolute(imgs[index].RelPath));
    }

    // ---------------------------------------------------------- Find & replace
    // Find & replace runs inside the WebView2 document (window.find + execCommand).
    private void ToggleFindBar()
    {
        if (EditorPane.Visibility != Visibility.Visible) return;
        bool show = FindBar.Visibility != Visibility.Visible;
        FindBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            FindBox.Focus(FocusState.Programmatic);
            FindBox.SelectAll();
        }
    }

    private void CloseFind_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        NoteWeb.Focus(FocusState.Programmatic);
    }

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { FindNext(); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { CloseFind_Click(sender, e); e.Handled = true; }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();

    private bool FindNext()
    {
        var q = FindBox.Text ?? "";
        if (q.Length == 0 || NoteWeb.CoreWebView2 is null) return false;
        _ = NoteWeb.CoreWebView2.ExecuteScriptAsync($"findNext({JsonSerializer.Serialize(q)})");
        return true;
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        var q = FindBox.Text ?? "";
        if (q.Length == 0 || NoteWeb.CoreWebView2 is null) return;
        _ = NoteWeb.CoreWebView2.ExecuteScriptAsync(
            $"replaceOne({JsonSerializer.Serialize(q)},{JsonSerializer.Serialize(ReplaceBox.Text ?? "")})");
    }

    private async void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        var q = FindBox.Text ?? "";
        if (q.Length == 0 || NoteWeb.CoreWebView2 is null) return;
        var raw = await NoteWeb.CoreWebView2.ExecuteScriptAsync(
            $"replaceAll({JsonSerializer.Serialize(q)},{JsonSerializer.Serialize(ReplaceBox.Text ?? "")})");
        int count = int.TryParse(raw, out var n) ? n : 0;
        FindBar.Visibility = Visibility.Collapsed;
        await ShowInfoAsync("Replace all", $"Replaced {count} occurrence(s).");
    }

    // ============================================================ Splitter
    private bool _dragging;
    private double _dragStartX;
    private double _dragStartWidth;

    private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _dragStartX = e.GetCurrentPoint(null).Position.X;
        _dragStartWidth = NoteListColumn.ActualWidth;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var dx = e.GetCurrentPoint(null).Position.X - _dragStartX;
        NoteListColumn.Width = new GridLength(Math.Clamp(_dragStartWidth + dx, 230, 600));
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _settings.Current.SidebarWidth = NoteListColumn.ActualWidth;
        _settings.Save();
    }

    private bool _railDragging;
    private double _railStartX;
    private double _railStartWidth;

    private void RailSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _railDragging = true;
        _railStartX = e.GetCurrentPoint(null).Position.X;
        _railStartWidth = RailColumn.ActualWidth;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void RailSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_railDragging) return;
        var dx = e.GetCurrentPoint(null).Position.X - _railStartX;
        RailColumn.Width = new GridLength(Math.Clamp(_railStartWidth + dx, 150, 360));
    }

    private void RailSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_railDragging) return;
        _railDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        _settings.Current.RailWidth = RailColumn.ActualWidth;
        _settings.Save();
    }

    // -------------------------------------------------- Drawer collapse / expand
    private bool _railOpen = true, _listOpen = true;
    private double _railSavedWidth = 194, _listSavedWidth = 300;

    private void ToggleRail_Click(object sender, RoutedEventArgs e)
    {
        if (_railOpen)
        {
            if (RailColumn.ActualWidth > 1) _railSavedWidth = RailColumn.ActualWidth;
            _railOpen = false;
            RailColumn.MinWidth = 0;
            AnimateColumn(RailColumn, RailColumn.ActualWidth, 0, () => RailCard.Visibility = Visibility.Collapsed);
        }
        else
        {
            _railOpen = true;
            RailCard.Visibility = Visibility.Visible;
            AnimateColumn(RailColumn, 0, _railSavedWidth, () => RailColumn.MinWidth = 150);
        }
        UpdateDrawerChrome();
    }

    private void ToggleList_Click(object sender, RoutedEventArgs e)
    {
        if (_listOpen)
        {
            if (NoteListColumn.ActualWidth > 1) _listSavedWidth = NoteListColumn.ActualWidth;
            _listOpen = false;
            NoteListColumn.MinWidth = 0;
            AnimateColumn(NoteListColumn, NoteListColumn.ActualWidth, 0, () => NoteListCard.Visibility = Visibility.Collapsed);
        }
        else
        {
            _listOpen = true;
            NoteListCard.Visibility = Visibility.Visible;
            AnimateColumn(NoteListColumn, 0, _listSavedWidth, () => NoteListColumn.MinWidth = 230);
        }
        UpdateDrawerChrome();
    }

    // Corner radii, margins, and splitter visibility for the current open/closed combination.
    private void UpdateDrawerChrome()
    {
        RailSplitter.Visibility = (_railOpen && _listOpen) ? Visibility.Visible : Visibility.Collapsed;
        RailShadowEdge.Visibility = (_railOpen && _listOpen) ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = _listOpen ? Visibility.Visible : Visibility.Collapsed;

        if (_railOpen && _listOpen)
        {
            RailCard.CornerRadius = new CornerRadius(8, 0, 0, 8);
            RailCard.Margin = new Thickness(8, 8, 0, 8);
            NoteListCard.CornerRadius = new CornerRadius(0, 8, 8, 0);
            NoteListCard.Margin = new Thickness(0, 8, 2, 8);
        }
        else if (_railOpen)          // list hidden — the rail is the whole drawer
        {
            RailCard.CornerRadius = new CornerRadius(8);
            RailCard.Margin = new Thickness(8, 8, 2, 8);
        }
        else if (_listOpen)          // rail hidden — the list is the whole drawer
        {
            NoteListCard.CornerRadius = new CornerRadius(8);
            NoteListCard.Margin = new Thickness(8, 8, 2, 8);
        }
    }

    // Smoothly animate a grid column's width (GridLength isn't animatable, so step it on a timer).
    private void AnimateColumn(ColumnDefinition col, double from, double to, Action? onDone = null)
    {
        var start = DateTime.Now;
        var dur = TimeSpan.FromMilliseconds(180);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(15);
        timer.Tick += (_, _) =>
        {
            var t = (DateTime.Now - start).TotalMilliseconds / dur.TotalMilliseconds;
            if (t >= 1) { col.Width = new GridLength(to); timer.Stop(); onDone?.Invoke(); }
            else { var eased = 1 - Math.Pow(1 - t, 3); col.Width = new GridLength(from + (to - from) * eased); }
        };
        timer.Start();
    }

    // ================================================================ Settings
    // ===================================================== Settings window
    private SettingsWindow? _settingsWindow;

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this, _settings, _notes, _paths, _ocr, _storage);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    private AboutWindow? _aboutWindow;

    private void AboutFooter_Click(object sender, RoutedEventArgs e)
    {
        if (_aboutWindow is null)
        {
            _aboutWindow = new AboutWindow();
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        }
        _aboutWindow.Activate();
    }

    // Live-apply hooks invoked by the Settings window so changes preview immediately.
    internal void ApplyThemeLive()
    {
        ApplyTheme();
        ApplyThemeSurfaces();   // light/dark switch changes whether chrome is tinted
        ApplyPaperStyle();      // re-normalize the open note's paper/ink for the new theme
    }

    internal void ApplyBackdropLive() => ApplyBackdrop();

    internal void ApplyTintLive()
    {
        ApplyPaperStyle();      // dark-mode page depth scales with intensity
        ApplyThemeSurfaces();   // chrome/desk tint scales with intensity
    }

    internal void ApplyPaperLive() => ApplyPaperStyle();   // page color / rule lines / paper type

    internal void ApplyFontLive()
    {
        if (NoteWeb.CoreWebView2 is not null)
            _ = NoteWeb.CoreWebView2.ExecuteScriptAsync(
                $"if(window.ed)ed.style.fontSize='{(int)Math.Round(_settings.Current.EditorFontSize)}px';");
    }

    internal void ApplySpellLive()
    {
        if (NoteWeb.CoreWebView2 is null) return;
        var on = _settings.Current.SpellCheck && !ContainsMyanmar(_current?.BodyPlain ?? "");
        _ = NoteWeb.CoreWebView2.ExecuteScriptAsync($"if(window.ed)ed.spellcheck={(on ? "true" : "false")};");
    }

    internal void ApplyAccentTheme()
    {
        App.ApplyAccent(_settings.Current.AccentColor);
        RefreshAccentLive();
        ApplyThemeSurfaces();
        ApplyPaperStyle();
    }

    internal bool SetQuickNoteHotkey(bool on) => TryEnableQuickNoteHotkey(on);

    // -------------------------------------------------------- Backlinks (wiki-links)
    private void LinksButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || _current is null) return;
        var flyout = new MenuFlyout();
        var marker = $"data-id=\"{_current.Id}\"";
        var backlinks = _notes.ListNotes()
            .Where(n => n.Id != _current.Id && n.Type == NoteType.Note && (n.BodyRtf ?? "").Contains(marker))
            .ToList();

        var header = new MenuFlyoutItem { Text = backlinks.Count == 0 ? "No linked references" : $"Linked references ({backlinks.Count})", IsEnabled = false };
        flyout.Items.Add(header);
        if (backlinks.Count > 0) flyout.Items.Add(new MenuFlyoutSeparator());
        foreach (var n in backlinks)
        {
            var mi = new MenuFlyoutItem { Text = EffectiveTitle(n) };
            var id = n.Id;
            mi.Click += (_, _) => ShowNote(id);
            flyout.Items.Add(mi);
        }
        flyout.ShowAt(btn);
    }

    // ----------------------------------------------------- Voice reader (TTS)
    private async void ReadAloud_Click(object sender, RoutedEventArgs e)
    {
        if (_ttsPlayer is not null) { StopTts(); return; }
        if (_current is null) return;
        var text = (_current.BodyPlain ?? "").Trim();
        if (text.Length == 0) { await ShowInfoAsync("Read aloud", "This note has no text to read."); return; }
        try
        {
            using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
            var stream = await synth.SynthesizeTextToStreamAsync(text);
            _ttsPlayer = new Windows.Media.Playback.MediaPlayer();
            _ttsPlayer.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(StopTts);
            _ttsPlayer.Source = Windows.Media.Core.MediaSource.CreateFromStream(stream, stream.ContentType);
            _ttsPlayer.Play();
            ReadAloudIcon.Glyph = "";   // Stop
        }
        catch (Exception ex) { _ttsPlayer = null; await ShowInfoAsync("Read aloud failed", ex.Message); }
    }

    private void StopTts()
    {
        try { _ttsPlayer?.Pause(); _ttsPlayer?.Dispose(); } catch { }
        _ttsPlayer = null;
        ReadAloudIcon.Glyph = "";   // Speaker
    }

    // ----------------------------------------------------- Audio recording
    private async void RecordAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) { await StopRecordingAsync(); return; }
        if (_current is null || _current.Type != NoteType.Note)
        {
            await ShowInfoAsync("Record audio", "Open a note to record audio into it.");
            return;
        }
        try
        {
            _capture = new Windows.Media.Capture.MediaCapture();
            await _capture.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Audio,
            });
            _recRel = $"attachments/audio/{_current.Id}/{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.m4a";
            var abs = _paths.ToAbsolute(_recRel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(abs)!);
            var file = await folder.CreateFileAsync(Path.GetFileName(abs), CreationCollisionOption.ReplaceExisting);
            var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateM4a(Windows.Media.MediaProperties.AudioEncodingQuality.Auto);
            await _capture.StartRecordToStorageFileAsync(profile, file);
            _recording = true;
            RecordIcon.Glyph = "";   // Stop
            RecordIcon.Foreground = new SolidColorBrush(Colors.OrangeRed);
        }
        catch (UnauthorizedAccessException)
        {
            try { _capture?.Dispose(); } catch { }
            _capture = null;
            await ShowInfoAsync("Microphone blocked",
                "Enable microphone access in Windows Settings → Privacy & security → Microphone, then try again.");
        }
        catch (Exception ex)
        {
            try { _capture?.Dispose(); } catch { }
            _capture = null;
            await ShowInfoAsync("Recording failed", ex.Message);
        }
    }

    private async Task StopRecordingAsync()
    {
        try { if (_capture is not null) await _capture.StopRecordAsync(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        _recording = false;
        RecordIcon.Glyph = "";   // Microphone
        RecordIcon.ClearValue(FontIcon.ForegroundProperty);
        if (NoteWeb.CoreWebView2 is not null && _recRel.Length > 0)
        {
            var url = "https://notes.local/" + _recRel.Replace('\\', '/');
            await NoteWeb.CoreWebView2.ExecuteScriptAsync($"insertAudio({JsonSerializer.Serialize(url)})");
        }
    }

    private void FocusSearch()
    {
        SearchBox.Focus(FocusState.Programmatic);
    }

    // Subtle, fast fade so switching notes feels smooth (kept short so it never adds latency).
    private void FadeInPaper()
    {
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0.6,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(110)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, PaperBorder);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    // ----------------------------------------------- Distraction-free focus mode
    private void FocusMode_Click(object sender, RoutedEventArgs e) => SetFocusMode(!_focusMode);

    private void SetFocusMode(bool on)
    {
        if (on == _focusMode) return;
        _focusMode = on;
        if (on)
        {
            _savedSidebarWidth = NoteListColumn.Width;
            RailColumn.MinWidth = 0;
            RailColumn.Width = new GridLength(0);
            NoteListColumn.MinWidth = 0;
            NoteListColumn.Width = new GridLength(0);
            RailCard.Visibility = Visibility.Collapsed;
            NoteListCard.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
            RailSplitter.Visibility = Visibility.Collapsed;
            ToolbarBar.Visibility = Visibility.Collapsed;
            FindBar.Visibility = Visibility.Collapsed;
            FocusExitButton.Visibility = Visibility.Visible;
        }
        else
        {
            RailColumn.MinWidth = 150;
            RailColumn.Width = new GridLength(194);
            NoteListColumn.MinWidth = 230;
            NoteListColumn.Width = _savedSidebarWidth.Value > 0 ? _savedSidebarWidth : new GridLength(300);
            RailCard.Visibility = Visibility.Visible;
            NoteListCard.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
            RailSplitter.Visibility = Visibility.Visible;
            ToolbarBar.Visibility = Visibility.Visible;
            FocusExitButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyTheme()
    {
        var theme = _settings.Current.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        if (Content is FrameworkElement root) root.RequestedTheme = theme;

        var resolvedDark = theme == ElementTheme.Dark ||
            (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        var tb = AppWindow.TitleBar;
        tb.ButtonForegroundColor = resolvedDark ? Colors.White : Colors.Black;
        tb.ButtonHoverForegroundColor = resolvedDark ? Colors.White : Colors.Black;
    }

    private void ApplyBackdrop()
    {
        SystemBackdrop = _settings.Current.Backdrop switch
        {
            AppBackdrop.Acrylic => new DesktopAcrylicBackdrop(),
            AppBackdrop.Solid => null,
            _ => new MicaBackdrop(),
        };
        if (Content is Panel p)
            p.Background = _settings.Current.Backdrop == AppBackdrop.Solid
                ? (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"]
                : null;
    }

    private Task ShowInfoAsync(string title, string message) =>
        new ContentDialog { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = Content.XamlRoot }
            .ShowAsync().AsTask();

    private void InitializeWithWindow(object target)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(target, hwnd);
    }

    private IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    // ===================================================== Window subclass
    private SUBCLASSPROC? _subclassProc;   // kept alive for the lifetime of the window

    private void InstallSubclass()
    {
        _subclassProc = (h, msg, w, l, _, _) =>
        {
            const uint WM_HOTKEY = 0x0312;
            const uint WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;
            if (msg == WM_HOTKEY && (int)w == HotkeyId)
                DispatcherQueue.TryEnqueue(QuickNote);
            else if (msg == WM_TRAYCALLBACK)
            {
                var evt = (uint)(l.ToInt64() & 0xFFFF);
                if (evt is WM_LBUTTONUP or WM_LBUTTONDBLCLK) DispatcherQueue.TryEnqueue(ShowFromTray);
                else if (evt == WM_RBUTTONUP) DispatcherQueue.TryEnqueue(ShowTrayMenu);
            }
            return DefSubclassProc(h, msg, w, l);
        };
        SetWindowSubclass(Hwnd, _subclassProc, (IntPtr)1, (IntPtr)0);
    }

    // ========================================================= System tray
    private const uint WM_TRAYCALLBACK = 0x8001;       // WM_APP + 1
    private NOTIFYICONDATA _nid;
    private bool _trayAdded;
    private bool _reallyQuit;

    private void AddTrayIcon()
    {
        var hIcon = LoadImage(IntPtr.Zero, IconPath(), 1 /*IMAGE_ICON*/, 0, 0, 0x00000010 | 0x00000040 /*LR_LOADFROMFILE|LR_DEFAULTSIZE*/);
        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = Hwnd,
            uID = 1,
            uFlags = 0x1 | 0x2 | 0x4, // MESSAGE | ICON | TIP
            uCallbackMessage = (int)WM_TRAYCALLBACK,
            hIcon = hIcon,
            szTip = "My Notebook",
        };
        _trayAdded = Shell_NotifyIcon(0 /*NIM_ADD*/, ref _nid);
    }

    private void RemoveTrayIcon()
    {
        if (!_trayAdded) return;
        Shell_NotifyIcon(2 /*NIM_DELETE*/, ref _nid);
        _trayAdded = false;
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
                                    Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        SaveWindowPlacement();
        if (_settings.Current.CloseToTray && !_reallyQuit)
        {
            args.Cancel = true;
            AppWindow.Hide();
        }
        else
        {
            RemoveTrayIcon();
        }
    }

    /// <summary>First run -> maximized; afterwards restore the last size/position/maximized.</summary>
    private void RestoreWindowPlacement()
    {
        var s = _settings.Current;
        var presenter = AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;

        // Only restore a saved rect when it's a sane size AND actually visible on a connected
        // display; otherwise fall back to maximized so the window always shows up.
        if (s.WindowWidth >= 600 && s.WindowHeight >= 400 &&
            IsRectOnScreen(s.WindowX, s.WindowY, s.WindowWidth, s.WindowHeight))
        {
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(s.WindowX, s.WindowY, s.WindowWidth, s.WindowHeight));
            if (s.WindowMaximized) presenter?.Maximize();
        }
        else
        {
            presenter?.Maximize();
        }
    }

    /// <summary>True when the rect has a meaningful overlap with some connected display's work area.</summary>
    private static bool IsRectOnScreen(int x, int y, int w, int h)
    {
        try
        {
            foreach (var area in Microsoft.UI.Windowing.DisplayArea.FindAll())
            {
                var wa = area.WorkArea;
                int ix = Math.Max(x, wa.X), iy = Math.Max(y, wa.Y);
                int ax = Math.Min(x + w, wa.X + wa.Width), ay = Math.Min(y + h, wa.Y + wa.Height);
                if (ax - ix > 120 && ay - iy > 80) return true;   // at least a grabbable slice is visible
            }
        }
        catch { }
        return false;
    }

    private void SaveWindowPlacement()
    {
        var presenter = AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        var maximized = presenter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
        _settings.Current.WindowMaximized = maximized;
        if (!maximized)   // keep the last restored bounds so un-maximize returns there
        {
            _settings.Current.WindowX = AppWindow.Position.X;
            _settings.Current.WindowY = AppWindow.Position.Y;
            _settings.Current.WindowWidth = AppWindow.Size.Width;
            _settings.Current.WindowHeight = AppWindow.Size.Height;
        }
        _settings.Save();
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
        try { SetForegroundWindow(Hwnd); } catch { }
    }

    private void ShowTrayMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, 1, "Open");
        AppendMenu(menu, 0, 2, "New thread");
        AppendMenu(menu, 0x800 /*MF_SEPARATOR*/, 0, null);
        AppendMenu(menu, 0, 3, "Quit");

        SetForegroundWindow(Hwnd);
        GetCursorPos(out var pt);
        int cmd = TrackPopupMenuEx(menu, 0x0100 /*TPM_RETURNCMD*/, pt.X, pt.Y, Hwnd, IntPtr.Zero);
        DestroyMenu(menu);

        if (cmd == 1) ShowFromTray();
        else if (cmd == 2) { ShowFromTray(); CreateAndOpen(NoteType.Thread, "Quick note"); }
        else if (cmd == 3) QuitApp();
    }

    private void QuitApp()
    {
        _reallyQuit = true;
        RemoveTrayIcon();
        Application.Current.Exit();
    }

    // =================================================== Quick Note hotkey
    private const int HotkeyId = 0xB001;
    private bool _hotkeyRegistered;

    private bool TryEnableQuickNoteHotkey(bool enable)
    {
        var hwnd = Hwnd;
        if (!enable)
        {
            if (_hotkeyRegistered) { UnregisterHotKey(hwnd, HotkeyId); _hotkeyRegistered = false; }
            return true;
        }
        if (_hotkeyRegistered) return true;
        // MOD_ALT=1, MOD_WIN=8, MOD_NOREPEAT=0x4000; VK_N=0x4E.
        _hotkeyRegistered = RegisterHotKey(hwnd, HotkeyId, 0x0001 | 0x0008 | 0x4000, 0x4E);
        return _hotkeyRegistered;
    }

    private void QuickNote()
    {
        ShowFromTray();
        CreateAndOpen(NoteType.Thread, "Quick note");
    }

    // ========================================================= P/Invoke
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint fuLoad);
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA pnid);

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
}

// ---- Sidebar tree node model ----------------------------------------------
public enum NodeKind { Folder, Note, SmartFolder, Group, Unfiled, Trash, TrashNote, AllNotes }

public sealed class NodeItem : System.ComponentModel.INotifyPropertyChanged
{
    public NodeKind Kind { get; set; }
    public long Id { get; set; }
    public string Query { get; set; } = "";
    public string Glyph { get; set; } = "";

    private string _title = "";
    public string Title
    {
        get => _title;
        set { _title = value; Raise(); }
    }

    private bool _pinned;
    public bool Pinned
    {
        get => _pinned;
        set { _pinned = value; Raise(); }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "(untitled)" : Title;
    public string DisplayText => $"{(Pinned ? "📌 " : "")}{Glyph} {DisplayTitle}";

    /// <summary>Segoe Fluent glyph for the folder rail (replaces the old emoji).</summary>
    public string IconGlyph => Kind switch
    {
        NodeKind.AllNotes   => "\uE8F1",   // Document / all notes
        NodeKind.Folder     => "\uE8B7",   // Folder
        NodeKind.Unfiled    => "\uE7C3",   // Page (loose notes)
        NodeKind.Group      => "\uE721",   // Search (smart folders header)
        NodeKind.SmartFolder=> "\uE721",   // Search
        NodeKind.Trash      => "\uE74D",   // Delete / trash
        _ => "\uE8B7",
    };

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise()
    {
        // Updating Title/Pinned refreshes the tree label (fixes stale "New note").
        PropertyChanged?.Invoke(this, new(nameof(DisplayText)));
        PropertyChanged?.Invoke(this, new(nameof(DisplayTitle)));
    }
}

// ---- Thread card + search suggestion --------------------------------------
public sealed class ThreadCard : System.ComponentModel.INotifyPropertyChanged
{
    public long Id { get; init; }                     // Images.id (for reorder/delete)
    public string Timestamp { get; init; } = "";
    public string Caption { get; init; } = "";
    public string Path { get; init; } = "";          // absolute path, full-res source
    public BitmapImage Bitmap { get; init; } = new(); // thumbnail (auto-fit in the card)

    // Timeline-rail node state (updated on load / reorder).
    private int _number;
    public int Number { get => _number; set { if (_number == value) return; _number = value; Raise(nameof(NumberText)); } }
    public string NumberText => _number.ToString();

    private bool _isFirst;
    public bool IsFirst { get => _isFirst; set { if (_isFirst == value) return; _isFirst = value; Raise(nameof(TopLineVisibility)); } }

    private bool _isLast;
    public bool IsLast { get => _isLast; set { if (_isLast == value) return; _isLast = value; Raise(nameof(BottomLineVisibility)); } }

    public Visibility TopLineVisibility => _isFirst ? Visibility.Collapsed : Visibility.Visible;     // no line above the first node
    public Visibility BottomLineVisibility => _isLast ? Visibility.Collapsed : Visibility.Visible;   // no line below the last node

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));

    public static ThreadCard From(ImageItem img, string absPath) => new()
    {
        Id = img.Id,
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(img.CreatedAt).LocalDateTime.ToString("g"),
        Caption = img.Caption,
        Path = absPath,                 // absolute path; full-res source for the 100% viewer
        Bitmap = Thumbnail(absPath),    // decoded at display size to save CPU + memory
    };

    private static BitmapImage Thumbnail(string absPath)
    {
        // Cards render at MaxHeight 480; decode ~960px wide (2x for high-DPI), not full res.
        var bmp = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = 960 };
        bmp.UriSource = new Uri(absPath);
        return bmp;
    }
}

public sealed class SearchSuggestion
{
    public long NoteId { get; }
    private readonly string _label;
    public SearchSuggestion(SearchHit h) { NoteId = h.NoteId; _label = h.Title; }
    public override string ToString() => _label;
}

/// <summary>Floating viewer: image at 100% native resolution, pan + pinch/Ctrl-scroll zoom.</summary>
/// <summary>Floating viewer: image at 100% native pixels, pan + zoom, with its own
/// title-bar icon and a context menu (view 100%, copy, export, delete).</summary>
public sealed class ImageViewerWindow : Window
{
    private readonly string _path;
    private readonly Func<Task>? _onDelete;
    private readonly ScrollViewer _scroller;
    private readonly Image _img;

    public ImageViewerWindow(string path, Func<Task>? onDelete = null)
    {
        _path = path;
        _onDelete = onDelete;
        Title = "Image — " + System.IO.Path.GetFileName(path);

        _img = new Image
        {
            Source = new BitmapImage(new Uri(path)),
            Stretch = Stretch.None,   // native pixels = 100%
        };
        _scroller = new ScrollViewer
        {
            Content = _img,
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = 0.1f,
            MaxZoomFactor = 6f,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Enabled,
            Background = new SolidColorBrush(Colors.Black),
        };

        var menu = new MenuFlyout();
        Add(menu, "View at 100%", () => _scroller.ChangeView(null, null, 1f));
        Add(menu, "Copy image", async () =>
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(_path);
                var dp = new DataPackage();
                dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                Clipboard.SetContent(dp);
            }
            catch { }
        });
        Add(menu, "Export image…", () => _ = ExportAsync());
        if (_onDelete is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add(menu, "Delete image", () => _ = DeleteAsync());
        }
        _scroller.ContextFlyout = menu;
        Content = _scroller;

        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        var aw = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
        aw.Resize(new Windows.Graphics.SizeInt32(1000, 760));
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) aw.SetIcon(icon);

        static void Add(MenuFlyout m, string text, Action onClick)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => onClick();
            m.Items.Add(item);
        }
    }

    private async Task ExportAsync()
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
        picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_path);
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try { File.Copy(_path, file.Path, overwrite: true); } catch { }
    }

    private async Task DeleteAsync()
    {
        if (_onDelete is null) return;
        await _onDelete();   // host removes the DB row + file and refreshes the thread
        Close();
    }
}
