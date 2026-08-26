#!/usr/bin/env python3
"""Generate IconGlyphs.cs from the Fluent UI System Icons metadata.

The bundled font is microsoft/fluentui-system-icons (MIT). This script maps Mailbox's
logical icon names onto its codepoints so that commands can reference icons by name and a
theme can swap the entire icon set without touching a command definition.

Usage:
    curl -fsSLO https://raw.githubusercontent.com/microsoft/fluentui-system-icons/main/fonts/FluentSystemIcons-Regular.json
    curl -fsSLO https://raw.githubusercontent.com/microsoft/fluentui-system-icons/main/fonts/FluentSystemIcons-Filled.json
    python3 tools/generate-icons.py FluentSystemIcons-Regular.json FluentSystemIcons-Filled.json
"""

import json
import pathlib
import sys

# Logical name -> Fluent base name. Add here, then regenerate.
WANTED = {
    "mail-new": "mail_add",
    "new-items": "document_add",
    "delete": "delete",
    "archive": "archive",
    "ignore": "dismiss_circle",
    "cleanup": "broom",
    "junk": "prohibited",
    "reply": "arrow_reply",
    "reply-all": "arrow_reply_all",
    "forward": "arrow_forward",
    "meeting": "calendar_ltr",
    "move": "folder_arrow_right",
    "rules": "flash",
    "quicksteps": "flash_sparkle",
    "unread": "mail_unread",
    "categorize": "tag",
    "flag": "flag",
    "search": "search",
    "address-book": "book_contacts",
    "filter": "filter",
    "send-receive": "arrow_sync",
    "work-offline": "plug_disconnected",
    "snooze": "clock",
    "source": "code",
    "tracker": "eye_off",
    "shield": "shield",
    "chevron-down": "chevron_down",
    "chevron-right": "chevron_right",
    "mail": "mail",
    "calendar": "calendar_ltr",
    "people": "people",
    "person-add": "person_add",
    "tasks": "task_list_square_ltr",
    "notes": "note",
    "journal": "book",
    "attach": "attach",
    "print": "print",
    "settings": "settings",
    "more": "more_horizontal",
    "undo": "arrow_undo",
    "pin": "pin",
    "folder": "folder",
    "folder-open": "folder_open",
    "star": "star",
    "warning": "warning",
    "info": "info",
    "dismiss": "dismiss",
    "redo": "arrow_redo",
    "send": "send",
    "update-folder": "folder_sync",
    "sr-groups": "folder_multiple",
    "show-progress": "arrow_sync_circle",
    "cancel": "dismiss_circle",
    "apps": "grid",
    "change-view": "arrow_swap",
    "view-settings": "settings_cog_multiple",
    "arrange": "arrow_sort",
    "reverse-sort": "arrow_sort_up",
    "spacing": "arrow_autofit_height",
    "layout": "panel_left",
    "reader": "reading_mode_mobile",
    "read-aloud": "sound_source",
    "avatar": "person_circle",
    "collapse-left": "chevron_left",
    "chevron-left": "chevron_left",
    "chevron-up": "chevron_up",
    "expand-right": "chevron_right",
    "dock": "open",
    "zoom-in": "add",
    "zoom-out": "subtract",
    "reading-pane": "panel_right",
    "message-preview": "panel_bottom",
    "importance": "important",
    # Low importance is a downward arrow in the reference, not a chevron — a chevron beside
    # a button reads as that button having a dropdown.
    "importance-low": "arrow_down",
    "reminder": "alert",
    "mark-complete": "checkmark_circle",
    "new-task": "task_list_add",
    "share": "share",
    "private": "lock_closed",
    "focus-time": "lightbulb",
    "today": "calendar_today",
    "next-7": "calendar_week_numbers",
    "day-view": "calendar_day",
    "work-week": "calendar_work_week",
    "week-view": "calendar_week_start",
    "month-view": "calendar_month",
    "schedule-view": "calendar_agenda",
    "add": "add",

    # ---- Calendar module (Phase 11) -----------------------------------------------------
    "new-appointment": "calendar_add",
    "open-calendar": "calendar_arrow_right",
    "calendar-groups": "calendar_multiple",
    "publish-calendar": "globe",
    "email-calendar": "calendar_reply",
    "goto-date": "calendar_search",
    "recurrence": "arrow_repeat_all",
    "time-scale": "clock",
    "overlay": "layer",
    "calendar-color": "color",
    "show-as": "circle_half_fill",
    "calendar-settings": "calendar_settings",
    "calendar-sync": "calendar_sync",
    "cancel-meeting": "calendar_cancel",
    "accept": "calendar_checkmark",
    "decline": "clock_dismiss",
    "daily-task-list": "task_list_square_ltr",
    "room-list": "conference_room",

    # ---- Tasks, Notes and Journal (Phase 13) --------------------------------------------
    "remove-from-list": "dismiss",
    "task-detailed": "apps_list_detail",
    "task-simple-list": "text_bullet_list",
    "todo-list": "task_list_ltr",
    "assign-task": "person_arrow_right",
    "status-report": "mail_arrow_up",
    "new-note": "note_add",
    "note-list": "document_bullet_list",
    "journal-entry": "notepad",
    "notes-icons": "grid",
    "last-seven-days": "history",
    "journal-timeline": "timeline",

    # ---- People module (Phase 12) -------------------------------------------------------
    "contact-group": "people_team",
    "open-item": "open",
    "new-email": "mail_add",
    "business-card": "contact_card_group",
    "phone": "phone",
    "list-view": "list",
    "move-to": "folder_arrow_right",
    "mail-merge": "mail_template",
    "open-folder": "folder_open",
    "new-folder": "folder_add",
    "delete-folder": "folder_prohibited",
    "follow-up": "flag",

    # ---- Compose window: Clipboard and Basic Text -------------------------------------
    "paste": "clipboard_paste",
    "cut": "cut",
    "copy": "copy",
    "format-painter": "paint_brush",
    "font": "text_font",
    "font-size": "text_font_size",
    "grow-font": "font_increase",
    "shrink-font": "font_decrease",
    "bold": "text_bold",
    "italic": "text_italic",
    "underline": "text_underline",
    "strikethrough": "text_strikethrough",
    "subscript": "text_subscript",
    "superscript": "text_superscript",
    "highlight": "highlight",
    "font-color": "text_color",
    "clear-formatting": "text_clear_formatting",
    "bullets": "text_bullet_list",
    "numbering": "text_number_list_ltr",
    "multilevel-list": "text_bullet_list_tree",
    "indent-increase": "text_indent_increase_ltr",
    "indent-decrease": "text_indent_decrease_ltr",
    "align": "text_align_left",
    "line-spacing": "text_line_spacing",
    "styles": "text_effects",
    "change-styles": "text_edit_style",
    "zoom": "zoom_in",

    # ---- Compose window: Names and Include --------------------------------------------
    "check-names": "person_available",
    "contact-card": "contact_card",
    "signature": "signature",
    "save": "save",

    # ---- Insert ------------------------------------------------------------------------
    "table": "table",
    "picture": "image",
    "stock-images": "image_multiple",
    "online-pictures": "image_globe",
    "shapes": "shapes",
    "icons": "emoji",
    "3d-models": "cube",
    "smartart": "organization",
    "chart": "chart_multiple",
    "link": "link",
    "equation": "math_formula",
    "symbol": "math_symbols",

    # ---- Options -----------------------------------------------------------------------
    "themes": "color",
    "theme-colors": "color_fill",
    "theme-effects": "wand",
    "page-color": "paint_bucket",
    "voting": "poll",
    "receipt": "mail_read",
    "delay-delivery": "clock_alarm",
    "direct-replies": "arrow_reply_all",
    "bcc": "mail_copy",
    "from-field": "person_mail",
    "encrypt": "lock_shield",
    "sign": "certificate",
    "permission": "lock_closed",

    # ---- Review ------------------------------------------------------------------------
    "spelling": "text_grammar_wand",
    "editor": "text_grammar_checkmark",
    "thesaurus": "book",
    "word-count": "text_word_count",
    "smart-lookup": "search_info",
    "language": "local_language",
    "accessibility": "accessibility_checkmark",
    "dictate": "mic",
    "immersive": "reading_mode_mobile",
    "lightbulb": "lightbulb",

    # The Folder, View and Send/Receive tabs of the classic ribbon.
    "conversation": "mail_multiple",
    "conversation-settings": "mail_settings",
    "arrange-date": "calendar_clock",
    "arrange-from": "mail_open_person",
    "arrange-to": "person_mail",
    "arrange-size": "slide_size",
    "add-columns": "table_add",
    "expand-collapse": "chevron_up_down",
    "folder-pane": "panel_left",
    "reading-pane": "panel_right",
    "todo-bar": "column_triple",
    "reminders-window": "alert",
    "new-window": "window_new",
    "close-all": "dismiss_square_multiple",
    "folder-rename": "rename",
    "folder-copy": "copy",
    "folder-move": "folder_arrow_right",
    "folder-delete": "folder_dismiss",
    "mark-all-read": "mail_read",
    "run-rules": "play",
    "sort-az": "text_sort_ascending",
    "delete-all": "delete_dismiss",
    "favorite-folder": "star",
    "autoarchive": "archive_settings",
    "folder-permissions": "key",
    "folder-properties": "document_bullet_list",
    "download-headers": "document_arrow_down",
    "mark-download": "arrow_download",
    "unmark-download": "document_prohibited",
    "process-headers": "checkmark",
}

SIZES = (16, 20, 24, 32)

HEADER = '''// <auto-generated>
//   Generated from the Fluent UI System Icons metadata (MIT, microsoft/fluentui-system-icons).
//   Regenerate with tools/generate-icons.py rather than editing by hand.
// </auto-generated>

#nullable enable

namespace Mailbox.Theming.Icons;

/// <summary>
/// Maps Mailbox's logical icon names onto glyphs in the bundled Fluent UI System Icons font.
/// </summary>
/// <remarks>
/// Commands reference icons by logical name, never by codepoint, so a theme can swap the whole
/// icon set without touching a single command definition — which is exactly what the
/// <c>icons.set</c> token does, routing every lookup through <see cref="IconSets.Active"/>.
/// <para>
/// Fluent ships separately drawn artwork per size rather than one outline scaled up — a 16px
/// glyph carries less detail than its 24px sibling by design. Asking for the size you will
/// actually render at is what keeps icons crisp.
/// </para>
/// </remarks>
public static class IconGlyphs
{
    /// <summary>Sizes the bundled fonts provide artwork for.</summary>
    public static readonly int[] Sizes = [16, 20, 24, 32];

    // Values are strings rather than chars: Fluent places some glyphs in supplementary
    // private-use planes above U+FFFF, which do not fit in a 16-bit char and need a
    // surrogate pair.
    private static readonly Dictionary<string, Dictionary<int, string>> Regular =
        new(StringComparer.OrdinalIgnoreCase)
    {
'''

MIDDLE = '''    };

    // The filled variants, for themes that ask for them. Only the names and sizes the filled
    // font actually draws are listed; a lookup that finds nothing here falls back to Regular,
    // so choosing the filled set can never lose an icon.
    private static readonly Dictionary<string, Dictionary<int, string>> Filled =
        new(StringComparer.OrdinalIgnoreCase)
    {
'''

FOOTER = '''    };

    public static IReadOnlyCollection<string> Names => Regular.Keys;

    public static bool Has(string name) => Regular.ContainsKey(name);

    /// <summary>
    /// The glyph for a logical name at the nearest available size, rounding down so a request
    /// never returns artwork drawn with more detail than the target can show. The active icon
    /// set answers first; the regular set is the floor under it.
    /// </summary>
    public static string Get(string name, int size = 20)
    {
        if (IconSets.Active == IconSets.Filled
            && Filled.TryGetValue(name, out var filled)
            && Nearest(filled, size) is { } filledGlyph)
        {
            return filledGlyph;
        }

        if (!Regular.TryGetValue(name, out var bySize))
        {
            throw new KeyNotFoundException(
                $"No icon named '{name}'. Add it to tools/generate-icons.py and regenerate.");
        }

        return Nearest(bySize, size)!;
    }

    private static string? Nearest(Dictionary<int, string> bySize, int size)
    {
        if (bySize.Count == 0) return null;
        if (bySize.TryGetValue(size, out var exact)) return exact;

        var best = bySize.Keys.Where(s => s <= size).DefaultIfEmpty(bySize.Keys.Min()).Max();
        return bySize[best];
    }

    /// <summary>Never throws. Returns an empty string for an unknown name.</summary>
    public static string GetOrEmpty(string name, int size = 20)
        => Regular.ContainsKey(name) ? Get(name, size) : string.Empty;

    /// <summary>The filled coverage for a name, for the tests that hold the two sets together.</summary>
    internal static bool HasFilled(string name) => Filled.ContainsKey(name);
}
'''


def resolve(metadata: dict, variant: str) -> tuple[dict[str, dict[int, int]], list[str]]:
    resolved: dict[str, dict[int, int]] = {}
    missing: list[str] = []

    for logical, base in sorted(WANTED.items()):
        found = {
            size: metadata[key]
            for size in SIZES
            if (key := f"ic_fluent_{base}_{size}_{variant}") in metadata
        }
        if found:
            resolved[logical] = found
        else:
            missing.append(f"{logical} -> {base}")

    return resolved, missing


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    metadata = json.loads(pathlib.Path(sys.argv[1]).read_text())
    filled_metadata = json.loads(pathlib.Path(sys.argv[2]).read_text())

    resolved, missing = resolve(metadata, "regular")
    filled, filled_missing = resolve(filled_metadata, "filled")
    if filled_missing:
        print(f"{len(filled_missing)} name(s) have no filled variant and fall back to regular.", file=sys.stderr)

    if missing:
        print("Unresolved icon names:", file=sys.stderr)
        for m in missing:
            print(f"  {m}", file=sys.stderr)
        return 1

    def escape(cp: int) -> str:
        # \uXXXX only covers the BMP; anything above needs the 8-digit \UXXXXXXXX form.
        return f'"\\u{cp:04X}"' if cp <= 0xFFFF else f'"\\U{cp:08X}"'

    def table(entries: dict[str, dict[int, int]]) -> str:
        return "".join(
            "        [\"{}\"] = new() {{ {} }},\n".format(
                name,
                ", ".join(f"[{s}] = {escape(cp)}" for s, cp in sorted(sizes.items())),
            )
            for name, sizes in sorted(entries.items())
        )

    out = pathlib.Path(__file__).parent.parent / "src/Mailbox.Theming/Icons/IconGlyphs.cs"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(HEADER + table(resolved) + MIDDLE + table(filled) + FOOTER)

    codepoints = {cp for sizes in resolved.values() for cp in sizes.values()}
    print(f"Wrote {out} — {len(resolved)} icons ({len(filled)} with filled variants), {len(codepoints)} glyphs.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
