#!/usr/bin/env bash
# Builds Mailbox and installs it for the current user only — no root, nothing outside $HOME.
#
# This is the build to test with, not the build to ship: packaging/build-release.sh makes the
# tarball and the .deb that go to users, and packaging/test-deb.sh has to pass before either does.
# What this makes is the same Release binary, installed where a desktop can launch it, so a
# testing session runs what a user would run rather than `dotnet run` with a developer's
# environment around it.
#
#   ~/.local/lib/mailbox            the application
#   ~/.local/bin/mailbox            the launcher on PATH
#   ~/.local/share/applications     the desktop entries, including the diagnostics one
#   ~/Desktop                       shortcuts, where the desktop puts them
#
# Re-running replaces the install in place. Nothing here touches mail, settings or keys: those
# live under XDG data and config and survive every reinstall.
set -euo pipefail
cd "$(dirname "$0")/.."

VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
PREFIX="$HOME/.local"
LIB="$PREFIX/lib/mailbox"
BIN="$PREFIX/bin"
APPS="$PREFIX/share/applications"
ICONS="$PREFIX/share/icons/hicolor"
DESKTOP="${XDG_DESKTOP_DIR:-$HOME/Desktop}"

echo "── building v$VER (Release, self-contained)"

# Self-contained for the same reason the release is: a testing session should not be able to fail
# because of which .NET happens to be on the machine that day.
dotnet publish src/Mailbox.App/Mailbox.App.csproj \
    -c Release -r linux-x64 --self-contained true \
    -o "$LIB.new" -v q

# Swapped rather than written over, so a build that fails part way leaves the working install
# alone rather than half-replacing it.
rm -rf "$LIB.old"
[ -d "$LIB" ] && mv "$LIB" "$LIB.old"
mv "$LIB.new" "$LIB"
rm -rf "$LIB.old"

cp LICENSE "$LIB/LICENSE"
cp packaging/NOTICES.txt "$LIB/NOTICES.txt"
mkdir -p "$LIB/assets/icons"
cp assets/icons/mailbox-*.png "$LIB/assets/icons/" 2>/dev/null || true

echo "── launcher"
mkdir -p "$BIN"
cat > "$BIN/mailbox" <<EOF
#!/usr/bin/env bash
exec "$LIB/mailbox" "\$@"
EOF
chmod +x "$BIN/mailbox"

# The diagnostics launcher. Same binary, two switches: every wire conversation written down, and
# the log kept at Debug. Separate rather than a flag on the ordinary one, because the reason to
# want it is "something went wrong and I am about to do it again", and hunting for a checkbox at
# that moment is the wrong shape.
cat > "$BIN/mailbox-diagnostics" <<EOF
#!/usr/bin/env bash
export MAILBOX_PROTOCOL_LOG=1
export MAILBOX_LOG_LEVEL=debug
echo "Mailbox — diagnostics mode"
echo "  application log:  \${XDG_STATE_HOME:-\$HOME/.local/state}/mailbox/logs"
echo "  protocol logs:    \${XDG_STATE_HOME:-\$HOME/.local/state}/mailbox/protocol"
echo
echo "The protocol logs hold the whole conversation with your mail servers — your"
echo "passwords are redacted, your messages are not. Read before sharing one."
echo
exec "$LIB/mailbox" "\$@"
EOF
chmod +x "$BIN/mailbox-diagnostics"

echo "── icons and desktop entries"
# The panel's icon is the mailbox itself — the drawing the reader gave us for the taskbar —
# rather than the tile the application wears inside its own title bar. Two icons on purpose:
# the tile identifies the window from within, and the panel wants the thing it is a picture of.
# mailbox-app-* is that drawing; the fallback keeps an older tree installable.
for size in 16 24 32 48 64 128 256 512; do
    src="assets/icons/mailbox-app-${size}.png"
    [ -f "$src" ] || src="assets/icons/mailbox-${size}.png"
    [ -f "$src" ] || continue
    install -Dm644 "$src" "$ICONS/${size}x${size}/apps/mailbox.png"
done

mkdir -p "$APPS"

# The shipped entry, pointed at this install rather than at a system one.
sed -e "s|^Exec=mailbox|Exec=$BIN/mailbox|" \
    -e "s|^TryExec=mailbox|TryExec=$BIN/mailbox|" \
    -e "s|^Exec=mailbox --|Exec=$BIN/mailbox --|" \
    packaging/mailbox.desktop > "$APPS/mailbox.desktop"

cat > "$APPS/mailbox-diagnostics.desktop" <<EOF
[Desktop Entry]
Type=Application
Version=1.5
Name=Mailbox (diagnostics)
GenericName=Email Client
Comment=Mailbox with protocol logging and debug logging on
Exec=$BIN/mailbox-diagnostics %U
TryExec=$BIN/mailbox-diagnostics
Icon=mailbox
Terminal=true
Categories=Network;Email;Office;
StartupNotify=true
StartupWMClass=mailbox
NoDisplay=false
EOF

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$APPS" || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -qtf "$ICONS" || true

echo "── desktop shortcuts"
mkdir -p "$DESKTOP"
for entry in mailbox mailbox-diagnostics; do
    cp "$APPS/$entry.desktop" "$DESKTOP/$entry.desktop"
    chmod +x "$DESKTOP/$entry.desktop"

    # What GNOME and KDE both want before they will run a .desktop file dropped on the desktop
    # rather than showing it as a text file. Harmless where it is not needed.
    command -v gio >/dev/null 2>&1 \
        && gio set "$DESKTOP/$entry.desktop" metadata::trusted true 2>/dev/null || true
done

echo
echo "Installed Mailbox v$VER for $USER."
echo "  run:          mailbox"
echo "  diagnostics:  mailbox-diagnostics   (protocol + debug logging, keeps a terminal open)"
echo "  shortcuts:    $DESKTOP"
echo
case ":$PATH:" in
    *":$BIN:"*) ;;
    *) echo "Note: $BIN is not on your PATH, so use the desktop shortcut or the full path." ;;
esac
