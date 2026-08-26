namespace Mailbox.Contacts;

/// <summary>
/// What an advanced find is looking for. Empty fields are not asked about.
/// </summary>
/// <remarks>
/// Here rather than beside the dialog that fills it in: which contacts answer a query is a rule
/// about contacts, and a rule worth a test is worth living where a test can reach it.
/// </remarks>
public sealed record AdvancedFind(string Name, string Company, string Address, string JobTitle)
{
    public bool IsEmpty =>
        Name.Length == 0 && Company.Length == 0 && Address.Length == 0 && JobTitle.Length == 0;

    /// <summary>Whether a row answers every field that was filled in.</summary>
    public bool Matches(ContactRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return Has(row.Named(), Name)
               && Has(row.Contact.Company, Company)
               && Has(row.Contact.PrimaryEmail, Address)
               && Has(row.Contact.JobTitle, JobTitle);
    }

    private static bool Has(string? value, string wanted)
        => wanted.Length == 0
           || (value ?? string.Empty).Contains(wanted, StringComparison.CurrentCultureIgnoreCase);
}

