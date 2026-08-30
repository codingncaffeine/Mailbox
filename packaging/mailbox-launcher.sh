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
# unit, and the named carve-outs below are the only places in it the application may write.
RUNTIME="${XDG_RUNTIME_DIR:-/tmp}/mailbox"

# The web engine builds its own sandbox in the runtime directory — bubblewrap bookkeeping
# under .flatpak, its D-Bus and accessibility proxy sockets under wpe — and it aborts the
# whole process the first time a message renders if it cannot. Two carve-outs rather than
# opening the runtime directory: everything else in there (other applications' sockets, the
# session's own) stays out of reach.
WPE_RUNTIME="${XDG_RUNTIME_DIR:-/tmp}/wpe"
WEBKIT_RUNTIME="${XDG_RUNTIME_DIR:-/tmp}/.flatpak"

# The write paths must exist before the namespace is built: a ReadWritePaths entry that is
# missing is skipped (the "-" prefix), and the application could then never create it.
mkdir -p "$DATA" "$CONFIG" "$STATE" "$RUNTIME" "$WPE_RUNTIME" "$WEBKIT_RUNTIME"

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
# namespaces, and taking them away would trade its sandbox for this one. The syscall filter
# admits @mount and seccomp for the same reason — bubblewrap assembles that sandbox out of
# mount, pivot_root and seccomp calls, and under @system-service alone its helper dies on
# SIGSYS the first time a message renders.
#
# ProtectKernelTunables, ProtectKernelLogs and ProtectHostname stay off, each proven alone to
# be fatal: all three overmount pieces of /proc, the kernel refuses to mount a fresh procfs in
# a user namespace while the parent's is partly masked, and the engine's sandbox needs that
# procfs — so any one of them turns the first rendered message into a crash. What they would
# mask is root's to write anyway (/proc/sys, /proc/kmsg), and sethostname is still refused by
# the syscall filter, so keeping the engine's own sandbox costs nothing it actually held.
#
# mincore is admitted by name: the EGL loader probes its own mappings with it while the web
# process brings up its display, and @system-service does not carry it — so the filter killed
# the web process with SIGSYS on the first page load, after every whole-process wall above had
# been taken down. The application stayed up and every message body was silently blank, which
# is the worst failure shape this launcher can produce: nothing crashed, nothing logged, and
# the reading pane's engine simply never finished a load. It is a read-only query about page
# residency — it writes nothing and reaches nothing outside the process's own address space.
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
    --property=ReadWritePaths="$WPE_RUNTIME" \
    --property=ReadWritePaths="$WEBKIT_RUNTIME" \
    --property=ReadWritePaths=-"$DOWNLOADS" \
    --property=PrivateTmp=yes \
    --property=CapabilityBoundingSet= \
    --property=RestrictSUIDSGID=yes \
    --property=LockPersonality=yes \
    --property=ProtectKernelModules=yes \
    --property=ProtectControlGroups=yes \
    --property=ProtectClock=yes \
    --property=RestrictRealtime=yes \
    --property=RestrictAddressFamilies="AF_UNIX AF_INET AF_INET6 AF_NETLINK" \
    --property=SystemCallFilter="@system-service @mount seccomp mincore" \
    "$@"
