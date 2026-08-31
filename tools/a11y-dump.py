#!/usr/bin/env python3
"""Read Mailbox back over the accessibility bus, the way a screen reader does.

Usage: a11y-dump.py [--wait S] [--listen S] [--drive] [--max-depth N] [--app SUBSTRING]

Finds the application on the AT-SPI bus, prints its tree (role, name, states,
interfaces), then optionally stays subscribed to the events a screen reader
keys on — selection, focus, children, name changes — and prints each as it
arrives. --drive goes one further: it focuses the message list from the AT
side and presses rows through their exposed select action, which is the
end-to-end proof that what the peers publish actually works a row.

Run it beside a posed application inside one D-Bus session. The private
session bus cannot systemd-activate the accessibility pair, so start it by
hand first:

    /usr/lib/at-spi-bus-launcher --launch-immediately &
    /usr/lib/at-spi2-registryd &

Needs python-gobject with the Atspi 2.0 typelib (at-spi2-core).
"""
import argparse
import sys
import time

import gi
gi.require_version("Atspi", "2.0")
from gi.repository import Atspi, GLib

INTERESTING_STATES = [
    Atspi.StateType.FOCUSABLE, Atspi.StateType.FOCUSED,
    Atspi.StateType.SELECTABLE, Atspi.StateType.SELECTED,
    Atspi.StateType.SHOWING, Atspi.StateType.VISIBLE,
    Atspi.StateType.CHECKABLE, Atspi.StateType.CHECKED,
    Atspi.StateType.EXPANDABLE, Atspi.StateType.EXPANDED,
    Atspi.StateType.MANAGES_DESCENDANTS, Atspi.StateType.ACTIVE,
]
STATE_NAMES = {
    Atspi.StateType.FOCUSABLE: "focusable", Atspi.StateType.FOCUSED: "FOCUSED",
    Atspi.StateType.SELECTABLE: "selectable", Atspi.StateType.SELECTED: "SELECTED",
    Atspi.StateType.SHOWING: "showing", Atspi.StateType.VISIBLE: "visible",
    Atspi.StateType.CHECKABLE: "checkable", Atspi.StateType.CHECKED: "CHECKED",
    Atspi.StateType.EXPANDABLE: "expandable", Atspi.StateType.EXPANDED: "expanded",
    Atspi.StateType.MANAGES_DESCENDANTS: "manages-descendants",
    Atspi.StateType.ACTIVE: "ACTIVE",
}


def states_of(obj):
    try:
        held = obj.get_state_set()
        return [STATE_NAMES[s] for s in INTERESTING_STATES if held.contains(s)]
    except Exception as e:
        return [f"<states? {e}>"]


def label(obj):
    try:
        role = obj.get_role_name()
    except Exception:
        role = "<role?>"
    try:
        name = obj.get_name() or ""
    except Exception:
        name = "<name?>"
    try:
        ifaces = [i for i in Atspi.Accessible.get_interfaces(obj)
                  if i not in ("Accessible", "Component", "Collection")]
    except Exception:
        ifaces = []
    parts = [role]
    if name:
        parts.append(f"“{name}”")
    st = states_of(obj)
    if st:
        parts.append("[" + " ".join(st) + "]")
    if ifaces:
        parts.append("{" + " ".join(ifaces) + "}")
    return "  ".join(parts)


def dump(obj, depth, max_depth, max_children, out):
    pad = "  " * depth
    out.write(pad + label(obj) + "\n")
    if depth >= max_depth:
        return
    try:
        n = obj.get_child_count()
    except Exception as e:
        out.write(pad + f"  <children? {e}>\n")
        return
    shown = min(n, max_children)
    for i in range(shown):
        try:
            child = obj.get_child_at_index(i)
        except Exception as e:
            out.write(pad + f"  <child {i}? {e}>\n")
            continue
        if child is None:
            out.write(pad + f"  <child {i} is null>\n")
            continue
        dump(child, depth + 1, max_depth, max_children, out)
    if n > shown:
        out.write(pad + f"  … {n - shown} more of {n} children\n")


def find_app(substring):
    desktop = Atspi.get_desktop(0)
    n = desktop.get_child_count()
    apps = []
    for i in range(n):
        app = desktop.get_child_at_index(i)
        if app is None:
            continue
        try:
            name = app.get_name() or ""
        except Exception:
            name = ""
        apps.append(name)
        if substring in name.lower():
            return app, apps
    return None, apps


def on_event(event):
    src = event.source
    try:
        where = f"{src.get_role_name()} “{src.get_name() or ''}”"
    except Exception:
        where = "<gone>"
    extra = ""
    if event.type.startswith("object:active-descendant") or "selection" in event.type:
        try:
            child = event.any_data
            if child is not None and hasattr(child, "get_role_name"):
                extra = f"  -> {child.get_role_name()} “{child.get_name() or ''}”"
        except Exception:
            pass
    print(f"EVENT {time.strftime('%H:%M:%S')} {event.type} d1={event.detail1} on {where}{extra}", flush=True)


def find_named(obj, role, name, depth=0):
    """First descendant with this role name whose name contains `name`."""
    try:
        if obj.get_role_name() == role and name in (obj.get_name() or ""):
            return obj
    except Exception:
        return None
    if depth > 20:
        return None
    try:
        n = obj.get_child_count()
    except Exception:
        return None
    for i in range(n):
        child = obj.get_child_at_index(i)
        if child is None:
            continue
        hit = find_named(child, role, name, depth + 1)
        if hit is not None:
            return hit
    return None


def drive(app):
    """Focus the message list from the AT side, then press rows through their
    Action interface, watching what a screen reader would be told."""
    target = find_named(app, "list", "Message list")
    if target is None:
        print("DRIVE: no Message list found", flush=True)
        return
    n = target.get_child_count()
    print(f"DRIVE: Message list has {n} children; grabbing focus", flush=True)
    try:
        target.get_component_iface().grab_focus()
    except Exception as e:
        print(f"DRIVE: grab_focus on list failed: {e}", flush=True)
    time.sleep(1.0)
    for i in (1, 2, 3):
        if i >= n:
            break
        row = target.get_child_at_index(i)
        try:
            act = row.get_action_iface()
            names = [act.get_action_name(k) for k in range(act.get_n_actions())]
            print(f"DRIVE: row {i} “{row.get_name() or ''}” actions={names}; invoking 0", flush=True)
            act.do_action(0)
        except Exception as e:
            print(f"DRIVE: row {i} action failed: {e}", flush=True)
        time.sleep(1.2)
    try:
        sel = target.get_selection_iface()
        print(f"DRIVE: list reports {sel.get_n_selected_children()} selected; "
              f"first: “{(sel.get_selected_child(0).get_name() if sel.get_n_selected_children() else '')}”",
              flush=True)
    except Exception as e:
        print(f"DRIVE: selection read-back failed: {e}", flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--wait", type=float, default=8.0, help="seconds to wait for the app to appear")
    ap.add_argument("--listen", type=float, default=0.0, help="seconds to stay subscribed for events")
    ap.add_argument("--max-depth", type=int, default=12)
    ap.add_argument("--max-children", type=int, default=40)
    ap.add_argument("--drive", action="store_true", help="focus the message list and press rows via AT-SPI")
    ap.add_argument("--app", default="mailbox")
    args = ap.parse_args()

    Atspi.init()
    deadline = time.time() + args.wait
    app = None
    seen = []
    while time.time() < deadline:
        app, seen = find_app(args.app)
        if app is not None:
            break
        time.sleep(0.5)
    if app is None:
        print(f"NO APP matching '{args.app}' on the a11y bus. Apps present: {seen}", flush=True)
        sys.exit(1)

    print(f"=== TREE of “{app.get_name()}” ===", flush=True)
    dump(app, 0, args.max_depth, args.max_children, sys.stdout)
    sys.stdout.flush()

    if args.listen > 0:
        print(f"=== LISTENING {args.listen}s ===", flush=True)
        listener = Atspi.EventListener.new(on_event)
        for kind in ("object:selection-changed",
                     "object:state-changed:focused",
                     "object:state-changed:selected",
                     "object:active-descendant-changed",
                     "object:children-changed",
                     "object:property-change:accessible-name",
                     "focus:"):
            listener.register(kind)
        if args.drive:
            GLib.timeout_add(1500, lambda: (drive(app), False)[1])
        loop = GLib.MainLoop()
        GLib.timeout_add(int(args.listen * 1000), loop.quit)
        loop.run()
        print("=== DONE LISTENING ===", flush=True)


if __name__ == "__main__":
    main()
