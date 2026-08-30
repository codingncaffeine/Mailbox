using Mailbox.Store.Pim;

namespace Mailbox.Store;

/// <summary>
/// The one set of colour categories, and what keeps every store in step with it.
/// </summary>
/// <remarks>
/// The design's "one colour category set applying to every item type in every module". The set itself
/// lives in the PIM store, which is the file every module shares; each mail account keeps a
/// mirror of it, because a message's categories are a join table pointing at rows in that
/// account's own file and the join needs something local to point at. The mirror is matched by
/// <em>name</em>, which is also what the calendar, task, note and contact items carry, because
/// names are what iCalendar and vCard put on the wire.
/// <para>
/// A rename or a delete cannot be finished here: an item's categories are written into its own
/// iCalendar or vCard text, and only the codecs know how to put them back. So both hand back the
/// items that carried the name, and whoever called is expected to rewrite them — which is what
/// the shell does, through the same save paths a person editing the item would go through, so the
/// change queues for the server like any other.
/// </para>
/// </remarks>
public sealed class CategoryBook(PimRepository pim, Func<IReadOnlyList<MailRepository>> mailboxes)
{
    private readonly PimRepository _pim = pim ?? throw new ArgumentNullException(nameof(pim));
    private readonly Func<IReadOnlyList<MailRepository>> _mailboxes = mailboxes ?? throw new ArgumentNullException(nameof(mailboxes));

    /// <summary>
    /// The six the reference ships with, in its own order, each named after its colour — which is
    /// what makes "Blue Category" blue on a note without anything else being read.
    /// </summary>
    public static IReadOnlyList<(string Name, string Token)> Defaults { get; } =
    [
        ("Blue Category", "category.blue"),
        ("Green Category", "category.green"),
        ("Orange Category", "category.orange"),
        ("Purple Category", "category.purple"),
        ("Red Category", "category.red"),
        ("Yellow Category", "category.yellow"),
    ];

    public IReadOnlyList<Category> All() => _pim.Categories();

    public Category? Named(string name) => _pim.CategoryNamed(name);

    /// <summary>
    /// Fills the set the first time it is asked for: whatever the mail accounts already had, and
    /// the reference's six if they had nothing.
    /// </summary>
    /// <remarks>
    /// Adopting rather than replacing, because an account made before there was one set has its
    /// own categories with messages assigned to them — dropping those would uncolour mail that a
    /// person coloured. Adoption keeps the name, so every one of those assignments still resolves.
    /// </remarks>
    public void EnsureDefaults()
    {
        if (All().Count > 0)
        {
            Mirror();
            return;
        }

        foreach (var mail in _mailboxes())
        {
            foreach (var category in mail.Categories())
            {
                _pim.AddCategory(category.Name, category.ColourToken, category.Shortcut);
            }
        }

        if (All().Count == 0)
        {
            foreach (var (name, token) in Defaults) _pim.AddCategory(name, token);
        }

        Mirror();
    }

    /// <summary>
    /// Puts the set into every mail account: anything missing is added and anything whose colour
    /// or shortcut has moved is brought back into line.
    /// </summary>
    /// <remarks>
    /// Additive on purpose. A mail-side category the set has never heard of is left alone rather
    /// than deleted, because deleting it would take every message's assignment with it —
    /// <see cref="Delete"/> is the one thing that removes a mirror row, and it does so knowing
    /// that is what was asked for.
    /// </remarks>
    public void Mirror()
    {
        var master = All();
        if (master.Count == 0) return;

        foreach (var mail in _mailboxes())
        {
            var mirror = mail.Categories();

            foreach (var category in master)
            {
                var existing = mirror.FirstOrDefault(c => string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    mail.AddCategory(category.Name, category.ColourToken, category.Shortcut);
                    continue;
                }

                if (existing.ColourToken != category.ColourToken) mail.RecolourCategory(existing.Id, category.ColourToken);
                if (existing.Shortcut != category.Shortcut) mail.SetCategoryShortcut(existing.Id, category.Shortcut);
            }
        }
    }

    public Category Add(string name, string colourToken, string? shortcut = null)
    {
        var made = _pim.AddCategory(name, colourToken, shortcut);
        Mirror();
        return made;
    }

    /// <summary>
    /// Renames a category everywhere it is stored by name.
    /// </summary>
    /// <returns>
    /// The PIM items that carried the old name, for the caller to rewrite — their categories are
    /// in their own iCalendar or vCard text, which only the codecs can put back.
    /// </returns>
    public IReadOnlyList<PimItem> Rename(long id, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (All().FirstOrDefault(c => c.Id == id) is not { } category) return [];

        var carried = _pim.ItemsWithCategory(category.Name);
        _pim.RenameCategory(id, newName);

        foreach (var mail in _mailboxes())
        {
            // By the old name: the mirror has not been renamed yet, and a message's assignment
            // points at the row rather than at the name, so renaming the row keeps every one.
            if (mail.Categories().FirstOrDefault(c => string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase)) is { } mirrored)
            {
                mail.RenameCategory(mirrored.Id, newName.Trim());
            }
        }

        return carried;
    }

    public void Recolour(long id, string colourToken)
    {
        if (All().FirstOrDefault(c => c.Id == id) is not { } category) return;

        _pim.RecolourCategory(id, colourToken);

        foreach (var mail in _mailboxes())
        {
            if (mail.Categories().FirstOrDefault(c => string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase)) is { } mirrored)
            {
                mail.RecolourCategory(mirrored.Id, colourToken);
            }
        }
    }

    public void SetShortcut(long id, string? shortcut)
    {
        if (All().FirstOrDefault(c => c.Id == id) is not { } category) return;

        _pim.SetCategoryShortcut(id, shortcut);

        foreach (var mail in _mailboxes())
        {
            if (mail.Categories().FirstOrDefault(c => string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase)) is { } mirrored)
            {
                mail.SetCategoryShortcut(mirrored.Id, shortcut);
            }
        }
    }

    /// <summary>
    /// Removes a category from the set and from every mail account's mirror, which takes its
    /// message assignments with it.
    /// </summary>
    /// <returns>The PIM items that carried it, for the caller to rewrite.</returns>
    public IReadOnlyList<PimItem> Delete(long id)
    {
        if (All().FirstOrDefault(c => c.Id == id) is not { } category) return [];

        var carried = _pim.ItemsWithCategory(category.Name);
        _pim.DeleteCategory(id);

        foreach (var mail in _mailboxes())
        {
            if (mail.Categories().FirstOrDefault(c => string.Equals(c.Name, category.Name, StringComparison.OrdinalIgnoreCase)) is { } mirrored)
            {
                mail.DeleteCategory(mirrored.Id);
            }
        }

        return carried;
    }

    /// <summary>
    /// The names an item should carry once a category has been renamed or removed: the same list
    /// with the old name swapped for the new one, or dropped when there is no new one.
    /// </summary>
    public static IReadOnlyList<string> Rewrite(IReadOnlyList<string> categories, string from, string? to)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var written = new List<string>(categories.Count);

        foreach (var category in categories)
        {
            if (!string.Equals(category, from, StringComparison.OrdinalIgnoreCase))
            {
                written.Add(category);
                continue;
            }

            // A rename that collides with a category the item already carries leaves one of them,
            // not two: an item categorised twice the same way is a list nobody wrote.
            if (to is { Length: > 0 } && !written.Contains(to, StringComparer.OrdinalIgnoreCase)) written.Add(to);
        }

        return written;
    }
}
