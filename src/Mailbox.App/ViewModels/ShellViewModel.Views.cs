using Mailbox.Core.Search;
using Mailbox.Core.Views;
using Mailbox.Store;
using Mailbox.Store.Lists;

namespace Mailbox.App.ViewModels;

/// <summary>
/// The message list's view — Change View and Advanced View Settings: which of the folder's
/// documents is in force, what it makes of the layout, the columns, the grouping and sort,
/// the filter and the conditional formatting — and the operations the View tab offers over it.
/// </summary>
/// <remarks>
/// A folder's view is one <see cref="MailView"/> document on its row; the shell reads it as
/// the folder is opened and writes it as the reader changes anything about it — Arrange By,
/// the sort direction, a dialog's OK. The three shipped views and the saved ones are what
/// Change View offers; Reset puts a folder back to the view it was made from.
/// </remarks>
public sealed partial class ShellViewModel
{
    /// <summary>How wide one character is taken to be, for "compact below N characters".</summary>
    private const double CharWidth = 7.0;

    private bool _applyingView;
    private (OpenAccount Account, long FolderId)? _viewFolder;
    private SearchQuery _viewFilter = SearchQuery.Parse(string.Empty);
    private IReadOnlyList<(ConditionalFormat Format, SearchQuery Query)> _viewFormats = [];
    private bool _unreadStyled = true;
    private bool _collapseAllNext;

    /// <summary>The view in force for the folder on screen.</summary>
    public MailView CurrentView
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(CurrentViewName));
            Raise(nameof(ViewColumns));
        }
    } = MailView.Compact;

    public string CurrentViewName => CurrentView.Name;

    /// <summary>The line layout's columns, for the header strip and the row's cells.</summary>
    public IReadOnlyList<ViewColumn> ViewColumns => CurrentView.Columns;

    /// <summary>
    /// True while the list draws the compact card — the Compact view in a list narrower than
    /// its threshold, or told to always. The header strip hides with it, as the reference's does.
    /// </summary>
    public bool CardRows
    {
        get;
        private set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(ShowColumnHeaders));
            Raise(nameof(RowHeight));
            Raise(nameof(ShowPreviewLine));
            Raise(nameof(ShowCardPreview));
        }
    }

    public bool ShowColumnHeaders => !CardRows;

    /// <summary>The list's width on screen, reported by the window; what decides card or line for Compact.</summary>
    public double ListWidth
    {
        get;
        set
        {
            if (Math.Abs(field - value) < 0.5) return;
            field = value;
            RecomputeLayout();
        }
    }

    /// <summary>The names Change View offers: the three that ship, then what the account has saved.</summary>
    public IReadOnlyList<string> ViewNames
    {
        get
        {
            var names = new List<string> { MailView.CompactName, MailView.SingleName, MailView.PreviewName };
            if (_viewFolder is { } where) names.AddRange(where.Account.Mail.Views().Select(v => v.Name));
            return names;
        }
    }

    // ---- Loading and applying ------------------------------------------------------------------

    /// <summary>Reads the folder's view — or the default — and applies it, before its rows are filled.</summary>
    private void LoadFolderView(OpenAccount account, long folderId)
    {
        _viewFolder = (account, folderId);
        var json = account.Mail.FolderView(folderId);
        Apply(json is null ? MailView.Compact : MailView.FromJson(json), persist: false);
    }

    /// <summary>Puts a view in force: the sort, the grouping, the layout, the columns, the filter, the formats.</summary>
    private void Apply(MailView view, bool persist)
    {
        _applyingView = true;
        try
        {
            CurrentView = view;

            _viewFilter = SearchQuery.Parse(view.Filter);

            // "Unread messages" is drawn by the theme's unread style, switched by its rule; every
            // other rule — the shipped Overdue, the reader's own — is matched row by row, first wins.
            _unreadStyled = view.Formats.FirstOrDefault(f => f.BuiltIn && f.Name == "Unread messages")?.Enabled ?? true;
            _viewFormats = [.. view.Formats.Where(f => f.Enabled && !(f.BuiltIn && f.Name == "Unread messages") && f.Condition.Trim().Length > 0)
                .Select(f => (f, SearchQuery.Parse(f.Condition)))];

            if (Enum.TryParse<Arrangement>(view.SortField, ignoreCase: true, out var sort)) Arrangement = sort;
            SortDescending = view.SortDescending;
            PreviewLines = view.Layout == ViewLayout.Single ? 0 : view.PreviewLines;

            // Group By's expand/collapse defaults: all shut, all open, or as they were.
            _collapseAllNext = view.GroupsExpanded == false;
            if (view.GroupsExpanded == true) _collapsed.Clear();

            // Format Columns' date formats, read by every row's date labels.
            MessageRow.DateFormats = view.ColumnFormats
                .Where(kv => ViewFields.IsDate(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value.DateFormat);

            RebuildColumns();
            RecomputeLayout();
        }
        finally
        {
            _applyingView = false;
        }

        if (persist) PersistView();
        Rebuild();
    }

    /// <summary>Writes the current view onto the folder on screen.</summary>
    private void PersistView()
    {
        if (_viewFolder is not { } where) return;
        where.Account.Mail.SetFolderView(where.FolderId, CurrentView.ToJson());
    }

    /// <summary>Arrange By and the sort arrow write themselves into the folder's view.</summary>
    private void RememberSort()
    {
        if (_applyingView || _viewFolder is null) return;
        var sorted = CurrentView with { SortField = Arrangement.ToString(), SortDescending = SortDescending };
        if (sorted == CurrentView) return;
        CurrentView = sorted;
        PersistView();
    }

    /// <summary>The header strip's columns from the view — the glyph ones narrow and unlabelled.</summary>
    private void RebuildColumns()
    {
        var columns = new List<MessageColumn>();
        foreach (var (id, header, width, isGlyph, sortField) in CurrentView.HeaderColumns())
        {
            var column = new MessageColumn(id, header, width, isGlyph);
            if (sortField is { } field)
            {
                column.Sort = new RelayCommand(() => SortBy(field));

                // Marked only when the list really is sorted by it, and marked with which way
                // round: pressing the sorted column again reverses it, so the arrow is the only
                // thing that says what pressing it will do next.
                if (!isGlyph && SortsBy(field)) column.SortMark = SortDescending ? " ▾" : " ▴";
            }

            columns.Add(column);
        }

        Columns = columns;
    }

    /// <summary>Card or line, from the layout, the mode and the width.</summary>
    private void RecomputeLayout()
    {
        var view = CurrentView;
        var card = view.Layout == ViewLayout.Compact && view.CompactMode switch
        {
            CompactMode.AlwaysCompact => true,
            CompactMode.AlwaysSingleLine => false,
            _ => ListWidth > 0 && ListWidth < view.CompactBelowChars * CharWidth,
        };

        if (card == CardRows) return;
        CardRows = card;
        if (!_applyingView) Rebuild();
    }

    /// <summary>The arrangement the list groups by: the sort's, unless Group By names another.</summary>
    private Arrangement GroupArrangement
        => CurrentView.GroupBy is { } by && Enum.TryParse<Arrangement>(by, ignoreCase: true, out var group) ? group : Arrangement;

    private bool GroupDescending => CurrentView.GroupBy is null ? SortDescending : !CurrentView.GroupAscending;

    /// <summary>The applied rule's name, for the harness — the paint itself cannot be read off a log.</summary>
    internal string AppliedFormatName(MessageRow row) => FormatFor(row)?.Name ?? string.Empty;

    /// <summary>The first conditional-formatting rule of the reader's own that a row meets, or null.</summary>
    private ConditionalFormat? FormatFor(MessageRow row)
    {
        if (_viewFormats.Count == 0) return null;
        var facts = row.Facts();
        foreach (var (format, query) in _viewFormats)
        {
            if (SearchMatcher.Matches(query, facts)) return format;
        }

        return null;
    }

    // ---- The View tab's operations -------------------------------------------------------------

    /// <summary>Change View: one of the three, or a saved view, by name — a fresh copy of it, put on the folder.</summary>
    public void ChangeView(string name)
    {
        var view = MailView.BuiltIn(name)
                   ?? (_viewFolder is { } where && where.Account.Mail.ViewNamed(name) is { } saved ? MailView.FromJson(saved.Definition) with { Name = saved.Name } : null);
        if (view is null) return;

        Apply(view, persist: true);
        StatusRight = $"View: {view.Name}.";
    }

    /// <summary>View Settings' OK: the edited view, in force and on the folder.</summary>
    public void UpdateView(MailView view) => Apply(view, persist: true);

    /// <summary>
    /// A column dragged to a width in the header strip: the view's column takes it, and the
    /// rows and the folder's saved view follow. By position in the header, which is the view's
    /// column order.
    /// </summary>
    public void ResizeColumn(int index, double width)
    {
        var columns = CurrentView.Columns;
        if (index < 0 || index >= columns.Count) return;
        var wanted = Math.Max(Views.ViewHeaderStrip.MinColumnWidth, Math.Round(width));
        if (Math.Abs(columns[index].Width - wanted) < 0.5) return;

        var resized = columns.ToList();
        resized[index] = resized[index] with { Width = wanted };
        UpdateView(CurrentView with { Columns = resized });
    }

    /// <summary>Reset View: the folder goes back to the view it was made from, as it came.</summary>
    public void ResetView()
    {
        var name = CurrentView.Name;
        var pristine = MailView.BuiltIn(name)
                       ?? (_viewFolder is { } where && where.Account.Mail.ViewNamed(name) is { } saved ? MailView.FromJson(saved.Definition) with { Name = saved.Name } : null)
                       ?? MailView.Compact;

        if (_viewFolder is { } folder && pristine.IsBuiltIn) folder.Account.Mail.SetFolderView(folder.FolderId, null);
        Apply(pristine, persist: !pristine.IsBuiltIn);
        StatusRight = $"View “{pristine.Name}” reset.";
    }

    /// <summary>Save Current View As a New View: by name, for this account's folders; the folder takes the new name.</summary>
    public bool SaveViewAs(string name)
    {
        if (_viewFolder is not { } where || string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        if (MailView.BuiltIn(trimmed) is not null) return false;

        var view = CurrentView with { Name = trimmed };
        where.Account.Mail.SaveView(trimmed, view.ToJson(), DateTimeOffset.UtcNow);
        Apply(view, persist: true);
        Raise(nameof(ViewNames));
        StatusRight = $"View “{trimmed}” saved.";
        return true;
    }

    /// <summary>Apply Current View to Other Mail Folders: the same document onto each.</summary>
    public int ApplyViewTo(IEnumerable<long> folderIds)
    {
        if (_viewFolder is not { } where) return 0;
        var json = CurrentView.ToJson();
        var count = 0;
        foreach (var id in folderIds.Distinct())
        {
            where.Account.Mail.SetFolderView(id, json);
            count++;
        }

        StatusRight = $"View “{CurrentView.Name}” applied to {count} folder{(count == 1 ? "" : "s")}.";
        return count;
    }

    /// <summary>The saved views changed under the menu: the names are read again.</summary>
    public void RaiseViewNames() => Raise(nameof(ViewNames));

    /// <summary>The account whose views are on offer, for the dialogs.</summary>
    public OpenAccount? ViewAccount => _viewFolder?.Account;

    /// <summary>The folder on screen, by id, for the dialogs that name it.</summary>
    public long? ViewFolderId => _viewFolder?.FolderId;
}
