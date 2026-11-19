#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
root="$PWD"
version=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' src/Fingering.App/Fingering.App.csproj)
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || exit 1
tag="v$version"
if [[ "${GITHUB_REF:-}" == refs/tags/* && "${GITHUB_REF_NAME}" != "$tag" ]]; then
  echo "Tag must match application version: $tag"; exit 1
fi
cd artifacts/release
expected=(
  "Fingering-$version-win-x64.exe"
  "Fingering-$version-win-arm64.exe"
  "fingering_${version}_amd64.deb"
  "fingering-${version}-1.x86_64.rpm"
  "Fingering-$version-osx-x64.dmg"
  "Fingering-$version-osx-arm64.dmg"
)
for asset in "${expected[@]}"; do test -s "$asset"; done
manifests=(SHA256SUMS-linux-x64.txt SHA256SUMS-osx-x64.txt SHA256SUMS-osx-arm64.txt SHA256SUMS-win-x64.txt SHA256SUMS-win-arm64.txt)
for manifest in "${manifests[@]}"; do sha256sum --check "$manifest"; done
sha256sum "${expected[@]}" > SHA256SUMS.txt
cp "$root/AUDIT.md" .
cp "$root/.github/RELEASE-NOTES.md" release-notes.md
printf '\nBuild: https://github.com/%s/actions/runs/%s\nSource: `%s`\n' \
  "$GITHUB_REPOSITORY" "$GITHUB_RUN_ID" "$GITHUB_SHA" >> release-notes.md
if gh release view "$tag" --repo "$GITHUB_REPOSITORY" >/dev/null 2>&1; then
  echo "Release $tag already exists; refusing to overwrite it."; exit 1
fi
gh release create "$tag" "${expected[@]}" SHA256SUMS.txt AUDIT.md \
  --repo "$GITHUB_REPOSITORY" --target "$GITHUB_SHA" --draft \
  --title "Fingering $version — Fingerspot Rewrite" --notes-file release-notes.md
asset_count=$(gh release view "$tag" --repo "$GITHUB_REPOSITORY" --json assets --jq '.assets | length')
[[ "$asset_count" == 8 ]] || { echo 'Release assets incomplete; leaving draft for inspection'; exit 1; }
gh release edit "$tag" --repo "$GITHUB_REPOSITORY" --draft=false --latest
