using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Mailbox.App.ViewModels;
using Mailbox.Core.Views;

namespace Mailbox.App.Views;

/// <summary>
/// The message list's column strip, built from the view's columns: one flat button per column,
/// the glyph ones narrow and unlabelled, the subject taking what the others leave.
/// </summary>
/// <remarks>
/// A grid rather than a stack, so it can share its column definitions with
/// <see cref="MessageCells"/> and the header cell sits over the row cell it names, whatever
/// the view says the columns are.
/// </remarks>
public sealed class ViewHeaderStrip : Grid
{
    public static readonly StyledProperty<IReadOnlyList<MessageColumn>?> ColumnsProperty =
        AvaloniaProperty.Register<ViewHeaderStrip, IReadOnlyList<MessageColumn>?>(nameof(Columns));

    public IReadOnlyList<MessageColumn>? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>
    /// Raised when a column's right edge has been dragged and let go: which column, and the
    /// width it was left at. The strip has already redrawn itself at that width; the shell
    /// writes it into the view, which is what the rows and the next launch read.
    /// </summary>
    public event EventHandler<ColumnResizedEventArgs>? ColumnResized;

    /// <summary>The narrowest a dragged column may be left — the glyph columns' own width.</summary>
    public const double MinColumnWidth = 24;

    /// <summary>The grab area either side of a column's right edge.</summary>
    private const double HandleWidth = 6;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColumnsProperty) Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        var columns = Columns ?? [];
        MessageCells.Define(this, columns.Select(c => (c.Width, c.Stretches, c.IsGlyph)));

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var text = new TextBlock
            {
                Text = column.Title,
                TextTrimming = column.IsGlyph ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                HorizontalAlignment = column.IsGlyph ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            };
            text.Classes.Add("small");
            text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("list.header.text.brush");

            var button = new Button
            {
                Content = text,
                Padding = column.IsGlyph ? new Thickness(0, 3) : new Thickness(4, 3),
                HorizontalContentAlignment = column.IsGlyph ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Command = column.Sort,
            };
            button.Classes.Add("flat");
            button[!Button.BorderBrushProperty] = new DynamicResourceExtension("border.subtle.brush");
            if (column.SortTip.Length > 0) ToolTip.SetTip(button, column.SortTip);

            SetColumn(button, i);
            Children.Add(button);

            // The right edge of a fixed-width column is a handle: drag it and the column is that
            // wide, as the reference's headers resize. A glyph column is as wide as its glyph and
            // has none; the subject takes what the others leave, so its edge is not a size.
            if (!column.IsGlyph && !column.Stretches && i < columns.Count - 1) Children.Add(Handle(i));
        }
    }

    private Control Handle(int index)
    {
        var handle = new Border
        {
            Width = HandleWidth,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, -HandleWidth / 2, 0),
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            ZIndex = 1,
        };
        SetColumn(handle, index);

        double startX = 0, startWidth = 0;
        var dragging = false;

        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            startX = e.GetPosition(this).X;
            startWidth = ColumnDefinitions[index].ActualWidth;
            dragging = true;
            e.Pointer.Capture(handle);
            e.Handled = true;
        };
        handle.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            // Live: the header follows the pointer; the rows follow when it is let go.
            var width = Math.Max(MinColumnWidth, startWidth + e.GetPosition(this).X - startX);
            ColumnDefinitions[index].Width = new GridLength(width, GridUnitType.Pixel);
            e.Handled = true;
        };
        handle.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            ColumnResized?.Invoke(this, new ColumnResizedEventArgs(index, Math.Round(ColumnDefinitions[index].Width.Value)));
            e.Handled = true;
        };

        return handle;
    }
}

public sealed class ColumnResizedEventArgs(int index, double width) : EventArgs
{
    public int Index { get; } = index;
    public double Width { get; } = width;
}

/// <summary>
/// One message's cells in the line layout, built from the view's columns to match the header
/// strip. Each cell binds to the row it is given as its data context, so the list's recycling
/// of rows costs nothing here.
/// </summary>
public sealed class MessageCells : Grid
{
    public static readonly StyledProperty<IReadOnlyList<ViewColumn>?> ColumnsProperty =
        AvaloniaProperty.Register<MessageCells, IReadOnlyList<ViewColumn>?>(nameof(Columns));

    public IReadOnlyList<ViewColumn>? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColumnsProperty) Rebuild();
    }

    /// <summary>The shared column definitions: fixed widths, the subject a star with its width as the minimum.</summary>
    internal static void Define(Grid grid, IEnumerable<(double Width, bool Stretches, bool IsGlyph)> columns)
    {
        foreach (var (width, stretches, _) in columns)
        {
            grid.ColumnDefinitions.Add(stretches
                ? new ColumnDefinition(1, GridUnitType.Star) { MinWidth = Math.Min(width, 80) }
                : new ColumnDefinition(width, GridUnitType.Pixel));
        }
    }

    private void Rebuild()
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        var columns = Columns ?? [];
        Define(this, columns.Select(c => (c.Width, c.Id == ViewFields.Subject, ViewFields.IsGlyph(c.Id))));

        for (var i = 0; i < columns.Count; i++)
        {
            var cell = Cell(columns[i].Id, i == columns.Count - 1);
            if (cell is null) continue;
            SetColumn(cell, i);
            Children.Add(cell);
        }
    }

    /// <summary>The control for one field of the row.</summary>
    private static Control? Cell(string field, bool last)
    {
        switch (field)
        {
            case ViewFields.Importance:
                return Glyph(nameof(MessageRow.ImportanceGlyph), "status.danger.brush");
            case ViewFields.Reminder:
                return Glyph(nameof(MessageRow.ReminderGlyph), "text.secondary.brush");
            case ViewFields.Icon:
            {
                var text = new TextBlock { Text = "✉", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                text.Classes.Add("small");
                text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");
                return text;
            }
            case ViewFields.Flag:
                return Glyph(nameof(MessageRow.FlagGlyph), "status.danger.brush");
            case ViewFields.Attachment:
            {
                var text = new TextBlock { Text = "\U0001F4CE", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                text.Classes.Add("small");
                text[!Visual.IsVisibleProperty] = new Binding(nameof(MessageRow.HasAttachment));
                text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("text.secondary.brush");
                return text;
            }
            case ViewFields.From:
                return Text(nameof(MessageRow.From), nameof(MessageRow.SenderWeight));
            case ViewFields.To:
                return Text(nameof(MessageRow.ToLine), nameof(MessageRow.SubjectWeight));
            case ViewFields.Subject:
                return Text(nameof(MessageRow.Subject), nameof(MessageRow.SubjectWeight));
            case ViewFields.Received:
            case ViewFields.Sent:
            {
                var text = Text(field == ViewFields.Received ? nameof(MessageRow.ReceivedLabel) : nameof(MessageRow.SentLabel), nameof(MessageRow.SubjectWeight));
                text.Classes.Add("small");
                // The hover actions swap in for the last date column, as the row's XAML did for its date.
                if (last) text.Name = "RowDate";
                return text;
            }
            case ViewFields.Size:
            {
                var text = Text(nameof(MessageRow.SizeLabel), nameof(MessageRow.SubjectWeight));
                text.Classes.Add("small");
                return text;
            }
            case ViewFields.Categories:
                return CategoryStrip();
            case ViewFields.Folder:
            {
                var text = Text(nameof(MessageRow.FolderLabel), nameof(MessageRow.SubjectWeight));
                text.Classes.Add("small");
                text.Classes.Add("secondary");
                return text;
            }
            case ViewFields.Mention:
                return null;
            default:
                return null;
        }
    }

    private static TextBlock Text(string path, string weightPath)
    {
        var text = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        text[!TextBlock.TextProperty] = new Binding(path);
        text[!TextBlock.FontWeightProperty] = new Binding(weightPath);
        text[!TextBlock.FontStyleProperty] = new Binding(nameof(MessageRow.FormatStyle));
        text[!TextBlock.ForegroundProperty] = new Binding(nameof(MessageRow.InkToken)) { Converter = Theming.InkTokenConverter.Instance };
        return text;
    }

    private static TextBlock Glyph(string path, string brushToken)
    {
        var text = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text[!TextBlock.TextProperty] = new Binding(path);
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension(brushToken);
        return text;
    }

    private static Control CategoryStrip()
    {
        var strip = new ItemsControl { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        strip[!ItemsControl.ItemsSourceProperty] = new Binding(nameof(MessageRow.CategoryTokens));
        strip.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 });
        strip.ItemTemplate = new FuncDataTemplate<string>((token, _) =>
        {
            var swatch = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2) };
            swatch[!Border.BackgroundProperty] = new Binding(".") { Converter = Theming.TokenBrushConverter.Instance };
            return swatch;
        });
        return strip;
    }
}
