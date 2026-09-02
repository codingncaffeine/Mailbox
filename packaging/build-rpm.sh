#!/usr/bin/env bash
# Packs an already-staged install tree into an .rpm.
#
#   packaging/build-rpm.sh <staged-root> <rpm-arch> <version> <output.rpm>
#
# Called by build-release.sh, which assembles the tree the .deb also installs, so the two
# packages put the same files in the same places and the launcher's library path is one string
# in one file for every packaging.
#
# rpmbuild is used where it exists and borrowed from a Fedora container where it does not, which
# is the ordinary case on the development box — Arch has no rpm. Neither available is not a
# failure of the build: the tarball and the .deb are still made, and this says why it stopped.
#
# The dependency list is GENERATED rather than written down. rpm reads every bundled object's
# ELF headers and asks for the sonames they actually need, which is right on any RPM
# distribution without mapping Debian's package names onto anyone else's; the bundle's own
# libraries are filtered out of both halves, since a self-contained publish brings them and
# nothing outside the package should be able to satisfy them either. What is left over is what
# no ELF header records: the libraries the runtime and the engine open by name at run time.
set -euo pipefail
cd "$(dirname "$0")/.."

# Absolute throughout: the container path binds these as volumes, and podman rejects a relative
# name outright ("names must match ...") rather than resolving it against the working directory.
ROOT="$(realpath "${1:?staged root}")"
ARCH="${2:?rpm architecture}"
VER="${3:?version}"
DEST="$(realpath -m "${4:?output path}")"

IMAGE="${IMAGE:-fedora:42}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# The bundle's own shared objects, as one alternation. rpm would otherwise advertise every one
# of them to the whole system as a provided soname, and demand the ones they need from it.
BUNDLED=$(find "$ROOT/usr/lib/mailbox" -name '*.so*' -printf '%f\n' \
    | sed 's/\.so.*$//' | sort -u | paste -sd'|' -)

cat > "$WORK/mailbox.spec" <<SPEC
# A self-contained publish: the runtime, Skia, HarfBuzz and SQLite travel with the application
# in a private directory. Stripping breaks the bundle, there is nothing to make a debuginfo
# package out of, and the shebangs are the launcher's own.
%global debug_package %{nil}
%global __brp_strip %{nil}
%global __brp_strip_static_archive %{nil}
%global __brp_strip_comment_note %{nil}
%global __brp_mangle_shebangs %{nil}
%global __brp_check_rpaths %{nil}

# The bundled libraries are private to this package: not advertised, and not asked for.
%global __provides_exclude ^($BUNDLED)\\\\.so.*\$
%global __requires_exclude ^($BUNDLED)\\\\.so.*\$

Name:           mailbox
Version:        $VER
Release:        1%{?dist}
Summary:        Desktop mail client with a ribbon, calendar peek and reading pane
License:        GPL-3.0-or-later
URL:            https://github.com/codingncaffeine/Mailbox

# No BuildArch here: it is the one rpmbuild checks against what the host can build, and an
# aarch64 package made on an x86_64 box is "No compatible architectures found for build". The
# architecture is set by --target instead, which is a label rather than a claim about the
# builder — right, because nothing is compiled at this point. The binaries were cross-published
# by the .NET SDK and rpm reads their real architecture out of their ELF headers.

# What the generated list cannot find: everything opened by name at run time rather than linked,
# which on this application is most of the platform. Avalonia dlopens its X11, GL and text
# libraries; the .NET runtime dlopens ICU and refuses to start without it. Asked for by soname
# rather than by package name so the list is right on any RPM distribution — Fedora, openSUSE and
# their derivatives name these packages differently and provide the same sonames. (64bit) is how
# rpm qualifies a provide on both of the architectures built here.
Requires:       libicu
Requires:       libz.so.1()(64bit)
Requires:       libfreetype.so.6()(64bit)
Requires:       libglib-2.0.so.0()(64bit)
Requires:       libX11.so.6()(64bit)
Requires:       libXext.so.6()(64bit)
Requires:       libXi.so.6()(64bit)
Requires:       libXrandr.so.2()(64bit)
Requires:       libXcursor.so.1()(64bit)
Requires:       libICE.so.6()(64bit)
Requires:       libSM.so.6()(64bit)
Requires:       libEGL.so.1()(64bit)
Requires:       libGL.so.1()(64bit)

# The Wayland backend and the reading pane's engine, neither of which is needed to start: the
# window is drawn through X11 unless MAILBOX_WAYLAND asks otherwise, and the pane renders text
# where no engine can draw into it.
Recommends:     libwayland-client.so.0()(64bit)
Recommends:     libwayland-egl.so.1()(64bit)
Recommends:     libwayland-cursor.so.0()(64bit)
Recommends:     libwayland-server.so.0()(64bit)
Recommends:     libxkbcommon.so.0()(64bit)
Recommends:     libgbm.so.1()(64bit)
Recommends:     libdrm.so.2()(64bit)
Recommends:     libsoup-3.0.so.0()(64bit)

# The reading pane's engine. Weak deliberately, and by soname rather than by package name: WPE
# WebKit is not packaged by every distribution — Fedora packages none of it, openSUSE calls it
# libWPEWebKit-2_0-1 — and the pane renders the message as text where no engine can draw into it
# rather than showing a blank body. Asked for this way it is installed wherever it exists, under
# whatever name, and ignored where it does not.
Recommends:     libWPEWebKit-2.0.so.1()(64bit)
Recommends:     libwpe-1.0.so.1()(64bit)
Recommends:     libWPEBackend-fdo-1.0.so.1()(64bit)

# The two command-line tools the application shells out to, asked for by the path it calls them
# at: the package that carries secret-tool is libsecret on one distribution and libsecret-tools
# on another, and the file is the same either way.
Recommends:     /usr/bin/secret-tool
Recommends:     /usr/bin/notify-send

# The LDAP library, for looking people up in a company directory. By soname again: Fedora calls
# the package openldap and Debian calls it libldap2, and both provide this.
Recommends:     libldap.so.2()(64bit)

# Integrations the application does without: the portal answers the file dialogs, GTK is the
# fallback where none does.
Recommends:     xdg-desktop-portal
Recommends:     gtk3
Suggests:       hunspell-en-US
Suggests:       google-carlito-fonts
Suggests:       google-crosextra-caladea-fonts

%description
Mailbox reads and writes mail over POP3, IMAP and SMTP, files it in a local
store with instant search, rules, quick steps, junk filtering and follow-up
flags, and integrates with the desktop: mailto: links, notifications, the
tray, autostart. Passwords go to the desktop keyring through secret-tool.

The reading pane renders mail through WPE WebKit where the distribution
packages it. Where it does not, the message is rendered as text: the pane
refuses an engine that cannot paint into it rather than drawing nothing.

%install
cp -a %{_sourcedir}/. %{buildroot}/

%files
/usr/lib/mailbox
/usr/bin/mailbox
/usr/share/applications/mailbox.desktop
/usr/share/metainfo/io.github.codingncaffeine.Mailbox.metainfo.xml
/usr/share/icons/hicolor/*/apps/mailbox.png
%dir /usr/share/doc/mailbox
%license /usr/share/doc/mailbox/copyright
/usr/share/doc/mailbox/NOTICES.txt
/usr/share/doc/mailbox/Selawik-LICENSE.txt

%changelog
* $(date -u '+%a %b %d %Y') Mailbox <codingncaffeine@users.noreply.github.com> - $VER-1
- See the release notes at %{url}/releases
SPEC

build_here() {
    rpmbuild -bb --quiet \
        --define "_topdir $WORK/rpmbuild" \
        --define "_sourcedir $ROOT" \
        --target "$ARCH" \
        "$WORK/mailbox.spec"
    cp "$WORK/rpmbuild/RPMS/$ARCH"/*.rpm "$DEST"
}

build_in_container() {
    # The container is the machine's own architecture whichever one is being packaged: rpmbuild
    # reads the target off --target and the ELF headers, and a foreign-architecture image cannot
    # run at all without an emulator registered. Pinned rather than left to the default, because
    # a local image tag can be pointing at a foreign build from something else entirely.
    local platform=""
    case "$(uname -m)" in
        x86_64)  platform=linux/amd64 ;;
        aarch64) platform=linux/arm64 ;;
    esac

    # The spec and the staged tree go in read-only; only the finished package comes back.
    podman run --rm ${platform:+--platform "$platform"} \
        -v "$ROOT:/src:ro" -v "$WORK:/work:ro" -v "$(dirname "$DEST"):/out" \
        "$IMAGE" bash -c '
set -eu
dnf install -y -q rpm-build > /dev/null 2>&1
rpmbuild -bb --quiet \
    --define "_topdir /tmp/rpmbuild" \
    --define "_sourcedir /src" \
    --target '"$ARCH"' \
    /work/mailbox.spec
cp /tmp/rpmbuild/RPMS/'"$ARCH"'/*.rpm /out/'"$(basename "$DEST")"'
' 2>&1 | grep -v -E 'overlay|graph driver|vfs' || true

    # The pipeline's own status is the last grep's, and a grep that filters away every line it
    # was given exits 1 — so the package itself is what says whether this worked.
    [ -f "$DEST" ]
}

if command -v rpmbuild > /dev/null 2>&1; then
    build_here
elif command -v podman > /dev/null 2>&1; then
    echo "   no rpmbuild here — borrowing one from $IMAGE"
    build_in_container
else
    echo "   no rpmbuild and no podman: install rpm-build, or podman to borrow one." >&2
    exit 1
fi

echo "   $(basename "$DEST"): $(rpm -qp --queryformat '%{NAME} %{VERSION}-%{RELEASE} %{ARCH}' "$DEST" 2>/dev/null || stat -c%s "$DEST") bytes"
