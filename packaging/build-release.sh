#!/usr/bin/env bash
# Builds the Linux release artifacts from one publish:
#   Mailbox-<ver>-linux-x64.tar.gz   self-contained; extract anywhere and run ./mailbox, or run
#                                    packaging/install-desktop-files.sh for the desktop entry
#   Mailbox.deb                      system install under /usr/lib/mailbox, launcher /usr/bin/mailbox
# The AUR package (packaging/aur/PKGBUILD) repackages the tarball from the GitHub release, so the
# tarball's name and layout are a contract with it — change both together.
#
# Every build is meant to be followed by packaging/test-deb.sh: the reading pane's engine and the
# rest of the native surface are exactly the dependencies that work on a development box and fail
# on a clean install.
set -euo pipefail
cd "$(dirname "$0")/.."

VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
OUT=packaging/out
PUB=$OUT/publish
rm -rf "$OUT" && mkdir -p "$PUB"

echo "── publish v$VER (self-contained linux-x64, Release)"
dotnet publish src/Mailbox.App/Mailbox.App.csproj -c Release -r linux-x64 \
    --self-contained true -o "$PUB" -v q

# What the licences ask to travel with the binaries, and what a person unpacking the tarball
# should find first.
cp LICENSE "$PUB/LICENSE"
cp packaging/NOTICES.txt "$PUB/NOTICES.txt"
cp assets/fonts/Selawik-LICENSE.txt "$PUB/Selawik-LICENSE.txt"

# The runtime's LTTng tracing shim is dlopened only when tracing is asked for, and it wants a
# library none of the targets ship. Tracing is not a mail feature; the dependency sweep's bar
# is that nothing in the package wants anything the package did not bring.
rm -f "$PUB/libcoreclrtraceptprovider.so"
mkdir -p "$PUB/packaging"
cp packaging/mailbox.desktop packaging/install-desktop-files.sh "$PUB/packaging/"
cp packaging/io.github.codingncaffeine.Mailbox.metainfo.xml "$PUB/packaging/"
# The AUR package builds from this tarball and installs the hardened launcher out of it, so
# the launcher has to travel: without this line the PKGBUILD's package() has nothing to read.
cp packaging/mailbox-launcher.sh "$PUB/packaging/"
mkdir -p "$PUB/assets/icons"
cp assets/icons/mailbox-*.png "$PUB/assets/icons/"

echo "── tarball"
tar -C "$PUB" -czf "$OUT/Mailbox-$VER-linux-x64.tar.gz" .

echo "── deb"
DEB=$OUT/debroot
rm -rf "$DEB"
mkdir -p "$DEB/DEBIAN" "$DEB/usr/lib/mailbox" "$DEB/usr/bin" "$DEB/usr/share/doc/mailbox"
cp -a "$PUB/." "$DEB/usr/lib/mailbox/"
# The desktop files are installed to their own places by the shared script; the copies inside
# the publish tree are for the tarball's reader and have no business under /usr/lib.
rm -rf "$DEB/usr/lib/mailbox/packaging" "$DEB/usr/lib/mailbox/assets"
rm -f "$DEB/usr/lib/mailbox/LICENSE" "$DEB/usr/lib/mailbox/NOTICES.txt" "$DEB/usr/lib/mailbox/Selawik-LICENSE.txt"
cp LICENSE "$DEB/usr/share/doc/mailbox/copyright"
cp packaging/NOTICES.txt "$DEB/usr/share/doc/mailbox/NOTICES.txt"
cp assets/fonts/Selawik-LICENSE.txt "$DEB/usr/share/doc/mailbox/Selawik-LICENSE.txt"
DESTDIR="$DEB" PREFIX=/usr bash packaging/install-desktop-files.sh > /dev/null
# The launcher confines the application in a hardened transient systemd user unit; it execs
# the binary directly where there is no user manager to ask.
sed 's|@LIB@|/usr/lib/mailbox|' packaging/mailbox-launcher.sh > "$DEB/usr/bin/mailbox"
chmod 755 "$DEB/usr/bin/mailbox"

INSTALLED_KB=$(du -sk "$DEB/usr" | cut -f1)
# Depends are Debian 13 (trixie) names, the primary .deb target; the ICU alternates reach
# back to older releases. WPE WebKit is what renders mail; without it the reading pane falls
# back to text. GTK is only for the file dialogs where no desktop portal answers.
cat > "$DEB/DEBIAN/control" <<CTRL
Package: mailbox
Version: $VER
Section: mail
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_KB
Depends: libc6, libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72, zlib1g, libx11-6, libxext6, libxi6, libxrandr2, libxcursor1, libice6, libsm6, libfontconfig1, libfreetype6, libegl1, libgl1, libglib2.0-0t64 | libglib2.0-0, libsoup-3.0-0, libwayland-client0, libwayland-egl1, libwayland-server0, libwayland-cursor0, libxkbcommon0, libgbm1, libdrm2, libwpewebkit-2.0-1, libwpe-1.0-1, libwpebackend-fdo-1.0-1
Recommends: libsecret-tools, libnotify-bin, hunspell-en-us, fonts-crosextra-carlito, fonts-crosextra-caladea, libgtk-3-0t64 | libgtk-3-0, xdg-desktop-portal
Suggests: hunspell-en-gb, fonts-liberation, fonts-noto-core
Maintainer: Mailbox <codingncaffeine@users.noreply.github.com>
Homepage: https://github.com/codingncaffeine/Mailbox
Description: Desktop mail client with a ribbon, calendar peek and reading pane
 Mailbox reads and writes mail over POP3, IMAP and SMTP, files it in a local
 store with instant search, rules, quick steps, junk filtering and follow-up
 flags, and integrates with the desktop: mailto: links, notifications, the
 tray, autostart. Passwords go to the desktop keyring through secret-tool.
CTRL
# Named for the person downloading it, not for an apt archive: a software centre titles a
# sideloaded file by its name up to the first dot, so mailbox_0.5.0_amd64.deb reads as
# "mailbox_0" in the installer. The version lives in the control file and the release tag.
dpkg-deb --build --root-owner-group "$DEB" "$OUT/Mailbox.deb" > /dev/null

rm -rf "$DEB"
echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
echo "── sha256 (for packaging/aur/PKGBUILD):"
sha256sum "$OUT/Mailbox-$VER-linux-x64.tar.gz" | cut -d' ' -f1
