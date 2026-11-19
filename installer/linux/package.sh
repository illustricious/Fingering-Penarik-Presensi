#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
version=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' src/Fingering.App/Fingering.App.csproj)
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid package version'; exit 1; }
root="$PWD"
publish="$root/artifacts/publish-linux-x64"
output="$root/artifacts/packages"
mkdir -p "$output"
dotnet publish src/Fingering.App -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$publish"
chmod 755 "$publish/Fingering"
"$publish/Fingering" --version
dotnet run --project installer/deb/MakeDeb -c Release -- \
  "$publish" "$version" "$output/fingering_${version}_amd64.deb" "$root/src/Fingering.App/Assets/icon.png"
dpkg-deb --info "$output/fingering_${version}_amd64.deb"
dpkg-deb --contents "$output/fingering_${version}_amd64.deb"
desktop-file-validate installer/linux/fingering.desktop

top="$root/artifacts/rpmbuild"
mkdir -p "$top/BUILD" "$top/BUILDROOT" "$top/RPMS" "$top/SOURCES" "$top/SPECS" "$top/SRPMS"
rpmbuild -bb installer/linux/fingering.spec --target x86_64 \
  --define "_topdir $top" --define "app_version $version" --define "source_root $root"
cp "$top/RPMS/x86_64/fingering-${version}-1.x86_64.rpm" "$output/"
rpm -qip "$output/fingering-${version}-1.x86_64.rpm"
rpm -qlp "$output/fingering-${version}-1.x86_64.rpm"
rpmcheck="$root/artifacts/rpm-check"
mkdir -p "$rpmcheck"
(cd "$rpmcheck" && rpm2cpio "$output/fingering-${version}-1.x86_64.rpm" | cpio -idm --quiet)
cmp "$publish/Fingering" "$rpmcheck/opt/illustricious/fingering/Fingering"
"$rpmcheck/opt/illustricious/fingering/Fingering" --version

# Verify the installed Debian payload and launch the first-run window on a virtual X11 display.
sudo dpkg -i "$output/fingering_${version}_amd64.deb"
fingering --version
set +e
xvfb-run -a timeout 10s fingering --data "$root/artifacts/smoke-data"
status=$?
set -e
[[ $status -eq 124 ]] || { echo "GUI smoke test exited unexpectedly: $status"; exit 1; }
(cd "$output" && sha256sum ./*.deb ./*.rpm > SHA256SUMS-linux-x64.txt)
