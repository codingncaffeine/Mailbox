#!/usr/bin/env bash
# Builds the Linux release artifacts, one publish per architecture:
#   Mailbox-<ver>-linux-<arch>.tar.gz  self-contained; extract anywhere and run ./mailbox, or run
#                                      packaging/install-desktop-files.sh for the desktop entry
#   Mailbox.deb / Mailbox-arm64.deb    system install under /usr/lib/mailbox, launcher /usr/bin/mailbox
#   Mailbox.rpm / Mailbox-aarch64.rpm  the same layout for Fedora, openSUSE and their relatives
#
#   bash packaging/build-release.sh            # x64, the default and the one the AUR reads
#   bash packaging/build-release.sh arm64      # aarch64 only
#   bash packaging/build-release.sh all        # both
#
# The x64 names are a contract: the AUR package (packaging/aur/PKGBUILD) repackages the tarball
# from the GitHub release, so its name and layout change together with that file. The .deb and
# .rpm are named for the person downloading them rather than for an archive — a software centre
# titles a sideloaded file by its name up to the first dot, so mailbox_0.5.0_amd64.deb read as
# "mailbox_0" in the installer. The version lives in the package metadata and the release tag.
#
# Cross-publishing is a second run of the same command: the runtime and every native library the
# build carries come out of the NuGet runtime pack for the target, so an aarch64 build off an
# x86_64 box ships aarch64 objects throughout (checked — every .so in the tree reports
# EM_AARCH64). What it cannot check is the machine's own libraries, which is why the dependency
# test below is worth running under emulation before an aarch64 release goes out.
#
# Every build is meant to be followed by packaging/test-deb.sh and packaging/test-rpm.sh: the
# reading pane's engine and the rest of the native surface are exactly the dependencies that work
# on a development box and fail on a clean install.
set -euo pipefail
cd "$(dirname "$0")/.."

VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
OUT=packaging/out

ARCHES=("${@:-x64}")
[ "${ARCHES[0]}" = "all" ] && ARCHES=(x64 arm64)

for arch in "${ARCHES[@]}"; do
    case "$arch" in
        x64|arm64) ;;
        *) echo "Unknown architecture '$arch' — expected x64, arm64 or all." >&2; exit 1 ;;
    esac
done

rm -rf "$OUT" && mkdir -p "$OUT"

# ---- one architecture ------------------------------------------------------------------------

build_one() {
    local arch="$1" pub="$OUT/publish-$1"
    # Debian and RPM each spell the same machine their own way, and neither spells it .NET's way.
    local debarch rpmarch suffix
    case "$arch" in
        x64)   debarch=amd64; rpmarch=x86_64;  suffix="" ;;
        arm64) debarch=arm64; rpmarch=aarch64; suffix="-arm64" ;;
    esac

    mkdir -p "$pub"

    echo "── publish v$VER (self-contained linux-$arch, Release)"
    dotnet publish src/Mailbox.App/Mailbox.App.csproj -c Release -r "linux-$arch" \
        --self-contained true -o "$pub" -v q

    # What the licences ask to travel with the binaries, and what a person unpacking the tarball
    # should find first.
    cp LICENSE "$pub/LICENSE"
    cp packaging/NOTICES.txt "$pub/NOTICES.txt"
    cp assets/fonts/Selawik-LICENSE.txt "$pub/Selawik-LICENSE.txt"

    # The runtime's LTTng tracing shim is dlopened only when tracing is asked for, and it wants a
    # library none of the targets ship. Tracing is not a mail feature; the dependency sweep's bar
    # is that nothing in the package wants anything the package did not bring.
    rm -f "$pub/libcoreclrtraceptprovider.so"
    mkdir -p "$pub/packaging"
    cp packaging/mailbox.desktop packaging/install-desktop-files.sh "$pub/packaging/"
    cp packaging/io.github.codingncaffeine.Mailbox.metainfo.xml "$pub/packaging/"
    # The AUR package builds from this tarball and installs the hardened launcher out of it, so
    # the launcher has to travel: without this line the PKGBUILD's package() has nothing to read.
    cp packaging/mailbox-launcher.sh "$pub/packaging/"
    mkdir -p "$pub/assets/icons"
    cp assets/icons/mailbox-*.png "$pub/assets/icons/"

    echo "── tarball (linux-$arch)"
    tar -C "$pub" -czf "$OUT/Mailbox-$VER-linux-$arch.tar.gz" .

    # ---- the tree both packages install, assembled once ---------------------------------------
    #
    # /usr/lib/mailbox rather than the RPM world's /usr/lib64: the launcher's library path is one
    # string in one file, installed identically by the tarball, the .deb, the .rpm and the AUR
    # package, and a private directory holding a self-contained bundle gains nothing from the
    # split that names it.
    local root="$OUT/root-$arch"
    rm -rf "$root"
    mkdir -p "$root/usr/lib/mailbox" "$root/usr/bin" "$root/usr/share/doc/mailbox"
    cp -a "$pub/." "$root/usr/lib/mailbox/"
    # The desktop files are installed to their own places by the shared script; the copies inside
    # the publish tree are for the tarball's reader and have no business under /usr/lib.
    rm -rf "$root/usr/lib/mailbox/packaging" "$root/usr/lib/mailbox/assets"
    rm -f "$root/usr/lib/mailbox/LICENSE" "$root/usr/lib/mailbox/NOTICES.txt" \
          "$root/usr/lib/mailbox/Selawik-LICENSE.txt"
    cp LICENSE "$root/usr/share/doc/mailbox/copyright"
    cp packaging/NOTICES.txt "$root/usr/share/doc/mailbox/NOTICES.txt"
    cp assets/fonts/Selawik-LICENSE.txt "$root/usr/share/doc/mailbox/Selawik-LICENSE.txt"
    DESTDIR="$root" PREFIX=/usr bash packaging/install-desktop-files.sh > /dev/null
    # The launcher confines the application in a hardened transient systemd user unit; it execs
    # the binary directly where there is no user manager to ask.
    sed 's|@LIB@|/usr/lib/mailbox|' packaging/mailbox-launcher.sh > "$root/usr/bin/mailbox"
    chmod 755 "$root/usr/bin/mailbox"

    echo "── deb ($debarch)"
    local deb="$OUT/debroot-$arch"
    rm -rf "$deb"
    cp -a "$root" "$deb"
    mkdir -p "$deb/DEBIAN"
    local installed_kb
    installed_kb=$(du -sk "$deb/usr" | cut -f1)
    # libldap is a Recommends rather than a Depends: it is what the LDAP address book opens by
    # name at run time, nobody without a company directory needs one, and the directory says so
    # rather than the application failing to start.
    #
    # Depends are Debian 13 (trixie) names, the primary .deb target; the ICU alternates reach
    # back to older releases. WPE WebKit is what renders mail; without it the reading pane
    # renders the message as text, which the pane now decides for itself rather than building an
    # engine that would draw nothing. GTK is only for the file dialogs where no desktop portal
    # answers.
    cat > "$deb/DEBIAN/control" <<CTRL
Package: mailbox
Version: $VER
Section: mail
Priority: optional
Architecture: $debarch
Installed-Size: $installed_kb
Depends: libc6, libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72, zlib1g, libx11-6, libxext6, libxi6, libxrandr2, libxcursor1, libice6, libsm6, libfontconfig1, libfreetype6, libegl1, libgl1, libglib2.0-0t64 | libglib2.0-0, libsoup-3.0-0, libwayland-client0, libwayland-egl1, libwayland-server0, libwayland-cursor0, libxkbcommon0, libgbm1, libdrm2, libwpewebkit-2.0-1, libwpe-1.0-1, libwpebackend-fdo-1.0-1
Recommends: libsecret-tools, libnotify-bin, hunspell-en-us, fonts-crosextra-carlito, fonts-crosextra-caladea, libgtk-3-0t64 | libgtk-3-0, xdg-desktop-portal, libldap2 | libldap-2.5-0
Suggests: hunspell-en-gb, fonts-liberation, fonts-noto-core
Maintainer: Mailbox <codingncaffeine@users.noreply.github.com>
Homepage: https://github.com/codingncaffeine/Mailbox
Description: Desktop mail client with a ribbon, calendar peek and reading pane
 Mailbox reads and writes mail over POP3, IMAP and SMTP, files it in a local
 store with instant search, rules, quick steps, junk filtering and follow-up
 flags, and integrates with the desktop: mailto: links, notifications, the
 tray, autostart. Passwords go to the desktop keyring through secret-tool.
CTRL
    dpkg-deb --build --root-owner-group "$deb" "$OUT/Mailbox$suffix.deb" > /dev/null
    rm -rf "$deb"

    echo "── rpm ($rpmarch)"
    if bash packaging/build-rpm.sh "$root" "$rpmarch" "$VER" "$OUT/Mailbox${suffix:+-$rpmarch}.rpm"; then
        :
    else
        echo "   skipped — see the message above."
    fi

    rm -rf "$root"
}

for arch in "${ARCHES[@]}"; do
    build_one "$arch"
done

echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
echo "── sha256 (for packaging/aur/PKGBUILD):"
for arch in "${ARCHES[@]}"; do
    printf '%s  %s\n' "$(sha256sum "$OUT/Mailbox-$VER-linux-$arch.tar.gz" | cut -d' ' -f1)" "linux-$arch"
done
