#!/bin/sh
# Launches Mailbox inside a hardened transient systemd user unit.
#
# The application's own posture is strong — the sanitizer cannot be bypassed, credentials live
# in the keyring, nothing phones home — so this is confinement: if the process is ever made to
# misbehave, the unit is what decides what it can reach. The sandbox deliberately keeps the
# holes a mail client is for: the session D-Bus (keyring, notifications), the display socket,
# and the network.
#
# Installed as the /usr/bin launcher by all three packagings, its library-path placeholder
# filled in at install time. Without a running systemd user manager — or with
# MAILBOX_NO_SANDBOX=1, the debugging escape — it execs the binary directly, so launching
# never breaks on an exotic session.

MAILBOX="@LIB@/mailbox"

if [ "${MAILBOX_NO_SANDBOX:-0}" = "1" ] || ! command -v systemd-run >/dev/null 2>&1 \
    || ! systemctl --user show-environment >/dev/null 2>&1; then
    exec "$MAILBOX" "$@"
fi

DATA="${XDG_DATA_HOME:-$HOME/.local/share}/mailbox"
CONFIG="${XDG_CONFIG_HOME:-$HOME/.config}/mailbox"
STATE="${XDG_STATE_HOME:-$HOME/.local/state}/mailbox"

# The theme browser's download cache — a handful of kilobyte files. Its own carve-out
# because everything else under ~/.cache stays out of reach, exactly as with the runtime
# directory's sockets.
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/mailbox"

# The single-instance socket's own directory: the runtime directory stays read-only to the
# unit, and the named carve-outs below are the only places in it the application may write.
RUNTIME="${XDG_RUNTIME_DIR:-/tmp}/mailbox"

# The write paths must exist before the namespace is built: a ReadWritePaths entry that is
# missing is skipped (the "-" prefix), and the application could then never create it.
mkdir -p "$DATA" "$CONFIG" "$STATE" "$CACHE" "$RUNTIME"

# Where a saved attachment, an export or a backup may land: the reader's own folders — the
# eight the desktop names (Desktop, Documents, Downloads, Music, Pictures, Videos, Public,
# Templates) — are writable, and everything else under $HOME stays read-only to the unit, which
# is the trade the confinement makes. It used to be Downloads alone, and a save to the Desktop
# failed with nothing said: the picker is the desktop's and knows nothing of the unit, so it is
# the application that meets the read-only file system — and it is told below exactly what was
# opened, so that it can say so and name the folders that would have worked.
#
# xdg-user-dir answers with $HOME itself for a folder it has no entry for, and $HOME is the one
# thing that must not be opened, so that answer is dropped. A folder that does not exist is
# left out rather than created, and skipped ("-" prefix) should it vanish before the unit starts.
user_dir() {
    case "$1" in
        DESKTOP) fallback="$HOME/Desktop" ;;
        DOCUMENTS) fallback="$HOME/Documents" ;;
        DOWNLOAD) fallback="$HOME/Downloads" ;;
        MUSIC) fallback="$HOME/Music" ;;
        PICTURES) fallback="$HOME/Pictures" ;;
        VIDEOS) fallback="$HOME/Videos" ;;
        PUBLICSHARE) fallback="$HOME/Public" ;;
        TEMPLATES) fallback="$HOME/Templates" ;;
        *) fallback="" ;;
    esac
    dir="$(command -v xdg-user-dir >/dev/null 2>&1 && xdg-user-dir "$1")"
    case "$dir" in ""|"$HOME"|"$HOME/") dir="$fallback" ;; esac
    printf '%s' "$dir"
}

DOWNLOADS="$(user_dir DOWNLOAD)"
PLACES=""
for name in DESKTOP DOCUMENTS DOWNLOAD MUSIC PICTURES VIDEOS PUBLICSHARE TEMPLATES; do
    dir="$(user_dir "$name")"
    case "$dir" in ""|"$HOME"|"$HOME/") continue ;; esac
    [ -d "$dir" ] || continue
    PLACES="${PLACES}${dir}
"
done

# The folder the Backup & Restore window was told to write to, read out of the settings so
# the wall opens exactly where the reader pointed and nowhere else. The naive extraction is
# fine here: the value is a path the picker chose, and a path with a quote in it has larger
# problems than this line. Skipped when unset ("-" prefix below), and created first because
# a ReadWritePaths entry that is missing is silently dropped.
BACKUP_DIR="$(sed -n 's/.*"backup\.directory"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$CONFIG/settings.json" 2>/dev/null | head -n 1)"
[ -n "$BACKUP_DIR" ] || BACKUP_DIR="$DOWNLOADS/Mailbox Backups"
mkdir -p "$BACKUP_DIR" 2>/dev/null || true

# The taskbar icon: the full mailbox while there is unread mail, the empty one once it has been
# read. Plasma's task manager and GNOME's dock draw the icon the desktop entry names, through the
# icon theme, so the drawing changes by rewriting the application's icon in the reader's own theme
# and telling the desktop. Both halves are outside the wall, and should be: the theme is every
# application's, and the service database the task manager reads again lives in ~/.cache with
# everybody else's. So the application only asks. It writes one word, full or empty, to a file in
# its runtime directory, and a path unit set up here, outside the wall, runs this same binary with
# --panel-icon whenever that file is replaced. That process reads the word and nothing else, writes
# the mailbox icon and nothing else, and is stopped if it takes a minute.
#
# One unit per login, per binary and per icon theme: a second launch finds it waiting, and a
# development build beside the installed one does not share it. It carries the variables that say
# where the theme and the database are and which language the database is named for, because the
# user manager's environment is not this shell's.
PANEL_REQUEST="$RUNTIME/panel-icon"
PANEL_UNIT="mailbox-panel-icon-$(printf '%s\n' "$MAILBOX" "${XDG_DATA_HOME:-}" | cksum | cut -d ' ' -f 1)"
if ! systemctl --user --quiet is-active "$PANEL_UNIT.path" 2>/dev/null; then
    PANEL_ENV=""
    for var in DBUS_SESSION_BUS_ADDRESS XDG_RUNTIME_DIR XDG_CURRENT_DESKTOP XDG_DATA_HOME XDG_DATA_DIRS \
        XDG_CONFIG_HOME XDG_CONFIG_DIRS XDG_CACHE_HOME LANG LANGUAGE LC_ALL LC_MESSAGES; do
        eval "value=\${$var+x}"
        [ -n "$value" ] && PANEL_ENV="$PANEL_ENV --setenv=$var"
    done
    # The names are split into words on purpose; none of them has a space or a pattern in it.
    # shellcheck disable=SC2086
    systemd-run --user --quiet --unit="$PANEL_UNIT" \
        --property=Description="Mailbox taskbar icon" \
        --property=RuntimeMaxSec=60 \
        --path-property=PathChanged="$PANEL_REQUEST" \
        $PANEL_ENV \
        "$MAILBOX" --panel-icon "$PANEL_REQUEST" >/dev/null 2>&1 || true
fi

# Asked for only when something is listening. Without the unit — a systemd too old for path
# units, say — the application tries the icon itself and says in its log that it could not.
PANEL_ASK=""
systemctl --user --quiet is-active "$PANEL_UNIT.path" 2>/dev/null && PANEL_ASK="$PANEL_REQUEST"

# The transient unit inherits the user manager's environment, not this shell's — so the
# variables that matter travel explicitly: the session's own, and every MAILBOX_* the harness
# or a terminal set.
set -- "$MAILBOX" "$@"
for var in DISPLAY WAYLAND_DISPLAY XAUTHORITY XDG_CURRENT_DESKTOP XDG_SESSION_TYPE \
    XDG_RUNTIME_DIR DBUS_SESSION_BUS_ADDRESS LANG LC_ALL LC_MESSAGES \
    XDG_DATA_HOME XDG_CONFIG_HOME XDG_STATE_HOME XDG_CACHE_HOME; do
    eval "value=\${$var+x}"
    [ -n "$value" ] && set -- "--setenv=$var" "$@"
done
for var in $(env | sed -n 's/^\(MAILBOX_[A-Z0-9_]*\)=.*/\1/p'); do
    set -- "--setenv=$var" "$@"
done

# The reader's folders, one ReadWritePaths each — and the same list handed to the application
# (colon-separated, as PATH is), with the backup folder, so that a save the unit refused can be
# explained with the folders it would have allowed. Globbing is off for the walk: a folder name
# is a name, not a pattern.
WRITABLE=""
IFS_SAVED="$IFS"
IFS='
'
set -f
for dir in $PLACES; do
    set -- "--property=ReadWritePaths=-\"$dir\"" "$@"
    WRITABLE="${WRITABLE:+$WRITABLE:}$dir"
done
set +f
IFS="$IFS_SAVED"
set -- "--setenv=MAILBOX_SANDBOX=1" "--setenv=MAILBOX_SANDBOX_WRITABLE=${WRITABLE:+$WRITABLE:}$BACKUP_DIR" "$@"
[ -n "$PANEL_ASK" ] && set -- "--setenv=MAILBOX_PANEL_ICON=$PANEL_ASK" "$@"

# MemoryDenyWriteExecute stays off: the .NET JIT needs W^X mappings and the runtime aborts
# without them, and Chromium's JavaScript engine is built the same way even with scripts off.
# RestrictNamespaces stays off: Chromium sandboxes the processes that read a message inside
# user, PID and network namespaces of their own, and taking namespaces away would trade that
# sandbox for this one — Mailbox then refuses to start Chromium and shows mail as text. The
# syscall filter admits @mount and seccomp for the same reason: the sandbox chroots its helpers
# into an empty directory and installs its own seccomp filter on each.
#
# ProtectKernelTunables, ProtectKernelLogs and ProtectHostname stay off: all three overmount
# pieces of /proc, and the kernel refuses a fresh procfs in a user namespace while the parent's
# is partly masked. What they would mask is root's to write anyway (/proc/sys, /proc/kmsg), and
# sethostname is still refused by the syscall filter.
#
# @pkey is admitted for Chromium's JavaScript engine, which takes a memory protection key for its
# own code pages as every helper starts, scripts on or off; without it the filter kills the
# sandbox's first process and Chromium aborts the application. The calls only change how this
# process may touch its own memory.
#
# mincore and openat2 are admitted by name, and RestrictSUIDSGID stays off. Both calls are
# read-only or symlink-safe opens that older @system-service sets lack; RestrictSUIDSGID cannot
# see openat2's mode bits (they travel in a struct, out of seccomp's sight) and refuses the whole
# call. What it held — creating set-id files — is held anyway: NoNewPrivileges means nothing in
# the unit gains from executing one, the capability set is empty, and everything outside the mail
# directories is read-only.
# A terminal launch gets a pty so Ctrl+C reaches the application; a desktop launch has no
# tty and takes the pipe.
IO=--pipe
[ -t 0 ] && IO=--pty

exec systemd-run --user --quiet --collect --wait "$IO" \
    --property=Description="Mailbox (hardened)" \
    --working-directory="$PWD" \
    --property=NoNewPrivileges=yes \
    --property=ProtectSystem=strict \
    --property=ProtectHome=read-only \
    --property=ReadWritePaths="$DATA" \
    --property=ReadWritePaths="$CONFIG" \
    --property=ReadWritePaths="$STATE" \
    --property=ReadWritePaths="$CACHE" \
    --property=ReadWritePaths="$RUNTIME" \
    --property=ReadWritePaths="-\"$BACKUP_DIR\"" \
    --property=PrivateTmp=yes \
    --property=CapabilityBoundingSet= \
    --property=LockPersonality=yes \
    --property=ProtectKernelModules=yes \
    --property=ProtectControlGroups=yes \
    --property=ProtectClock=yes \
    --property=RestrictRealtime=yes \
    --property=RestrictAddressFamilies="AF_UNIX AF_INET AF_INET6 AF_NETLINK" \
    --property=SystemCallFilter="@system-service @mount @pkey seccomp mincore openat2" \
    "$@"
