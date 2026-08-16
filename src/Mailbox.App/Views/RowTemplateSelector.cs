using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;
using Mailbox.App.ViewModels;

namespace Mailbox.App.Views;

/// <summary>
/// Picks the group-header template or the message template for a row.
/// </summary>
/// <remarks>
/// The list holds both kinds in one flat sequence so a single virtualizing panel draws the
/// whole folder. Avalonia's implicit template lookup does not reach an ItemsControl's items
/// reliably once the control theme supplies its own <c>ItemTemplate</c>, so the choice is made
/// here instead of left to resolution order — a template that silently falls back to
/// <c>ToString()</c> is a bug that renders rather than throws, which is the worst kind.
/// </remarks>
public sealed class RowTemplateSelector : IDataTemplate
{
    [Content]
    public IDataTemplate? Message { get; set; }

    public IDataTemplate? Header { get; set; }

    public IDataTemplate? Conversation { get; set; }

    /// <summary>The compact card, for a row the shell has marked <see cref="MessageRow.IsCard"/>.</summary>
    public IDataTemplate? Card { get; set; }

    /// <summary>The compact card for a folded conversation.</summary>
    public IDataTemplate? ConversationCard { get; set; }

    public bool Match(object? data) => data is MessageRow or GroupHeaderRow or ConversationRow;

    public Control? Build(object? data) => data switch
    {
        GroupHeaderRow => Header?.Build(data),
        ConversationRow { IsCard: true } when ConversationCard is not null => ConversationCard.Build(data),
        ConversationRow => Conversation?.Build(data),
        MessageRow { IsCard: true } when Card is not null => Card.Build(data),
        MessageRow => Message?.Build(data),
        _ => null,
    };
}
