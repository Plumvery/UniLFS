#!/bin/sh
# Replaces the bundled copy of mylfs (Tools~/mylfs) with a released version.
#   sh "Tools~/update-mylfs.sh" v0.2.0
# UniLFS runs every operation through this copy, so bump
# UniLfsMylfs.MinimumMylfsVersion too when the package starts relying on
# something the new version added.
set -eu
tag="${1:?usage: update-mylfs.sh <tag, e.g. v0.2.0>}"
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
curl -fsSL "https://codeload.github.com/Plumvery/mylfs/tar.gz/refs/tags/$tag" | tar -xz -C "$tmp"
src="$(find "$tmp" -mindepth 1 -maxdepth 1 -type d | head -n 1)"
rm -rf "$here/mylfs"
mkdir -p "$here/mylfs"
cp -R "$src/bin" "$src/src" "$src/package.json" "$src/LICENSE" "$here/mylfs/"
echo "Bundled mylfs $(node -p "require('$here/mylfs/package.json').version" 2>/dev/null || echo "$tag") into $here/mylfs"
