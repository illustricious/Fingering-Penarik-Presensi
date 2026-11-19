#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
rid="${1:-osx-arm64}"
case "$rid" in osx-arm64|osx-x64) ;; *) echo 'Use osx-arm64 or osx-x64'; exit 2;; esac
version=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' src/Fingering.App/Fingering.App.csproj)
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || exit 1
root="$PWD"
publish="$root/artifacts/publish-$rid"
stage="$root/artifacts/dmg-$rid"
app="$stage/Fingering.app"
output="$root/artifacts/packages"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$output"
dotnet publish src/Fingering.App -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=none -o "$publish"
cp -R "$publish/." "$app/Contents/MacOS/"
chmod 755 "$app/Contents/MacOS/Fingering"

iconset="$root/artifacts/Fingering-$rid.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" src/Fingering.App/Assets/icon.png --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" src/Fingering.App/Assets/icon.png --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/Fingering.icns"
cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>com.illustricious.fingering</string>
<key>CFBundleName</key><string>Fingering</string>
<key>CFBundleDisplayName</key><string>Fingering</string>
<key>CFBundleExecutable</key><string>Fingering</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>$version</string>
<key>CFBundleVersion</key><string>$version</string>
<key>CFBundleIconFile</key><string>Fingering.icns</string>
<key>LSMinimumSystemVersion</key><string>12.0</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
EOF
plutil -lint "$app/Contents/Info.plist"
# Ad-hoc signing permits local execution, including Apple Silicon. Developer ID
# signing/notarization requires the owner's Apple Developer credentials.
while IFS= read -r -d '' binary; do
  if file "$binary" | grep -q 'Mach-O'; then
    codesign --force --sign - --entitlements installer/macos/entitlements.plist "$binary"
  fi
done < <(find "$app/Contents/MacOS" -type f -print0)
codesign --force --sign - --entitlements installer/macos/entitlements.plist "$app"
codesign --verify --deep --strict --verbose=2 "$app"
"$app/Contents/MacOS/Fingering" --version
ln -s /Applications "$stage/Applications"
dmg="$output/Fingering-$version-$rid.dmg"
hdiutil create -volname Fingering -srcfolder "$stage" -ov -format UDZO "$dmg"
hdiutil verify "$dmg"
mountpoint="$root/artifacts/mount-$rid"
mkdir -p "$mountpoint"
hdiutil attach "$dmg" -mountpoint "$mountpoint" -nobrowse -readonly
trap 'hdiutil detach "$mountpoint"' EXIT
codesign --verify --deep --strict "$mountpoint/Fingering.app"
"$mountpoint/Fingering.app/Contents/MacOS/Fingering" --version
hdiutil detach "$mountpoint"
trap - EXIT
(cd "$output" && shasum -a 256 "$(basename "$dmg")" > "SHA256SUMS-$rid.txt")
