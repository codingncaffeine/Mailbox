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

# The single-instance socket's own directory: the runtime directory stays read-only to the
# unit, and this is the one place in it the application may bind.
RUNTIME="${XDG_RUNTIME_DIR:-/tmp}/mailbox"

# The write paths must exist before the namespace is built: a ReadWritePaths entry that is
# missing is skipped (the "-" prefix), and the application could then never create it.
mkdir -p "$DATA" "$CONFIG" "$STATE" "$RUNTIME"

# Where a saved attachment or an export lands by default. Everything else under $HOME is
# read-only to the unit; a save elsewhere fails with the picker's own error, which is the
# trade the confinement makes.
DOWNLOADS="$(command -v xdg-user-dir >/dev/null 2>&1 && xdg-user-dir DOWNLOAD || echo "$HOME/Downloads")"

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

# MemoryDenyWriteExecute stays off: the .NET JIT needs W^X mappings and the runtime aborts
# without them. RestrictNamespaces stays off: the web engine builds its own sandbox out of
# namespaces, and taking them away would trade its sandbox for this one.
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
    --property=ReadWritePaths="$RUNTIME" \
    --property=ReadWritePaths=-"$DOWNLOADS" \
    --property=PrivateTmp=yes \
    --property=CapabilityBoundingSet= \
    --property=RestrictSUIDSGID=yes \
    --property=LockPersonality=yes \
    --property=ProtectKernelTunables=yes \
    --property=ProtectKernelModules=yes \
    --property=ProtectKernelLogs=yes \
    --property=ProtectControlGroups=yes \
    --property=ProtectClock=yes \
    --property=ProtectHostname=yes \
    --property=RestrictRealtime=yes \
    --property=RestrictAddressFamilies="AF_UNIX AF_INET AF_INET6 AF_NETLINK" \
    --property=SystemCallFilter=@system-service \
    "$@"
