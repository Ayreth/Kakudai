#!/bin/sh
# Wraps a `dotnet publish` of Kakudai.Desktop into Kakudai.app, ad-hoc signed (Apple Silicon runs nothing unsigned).
# Usage: make-app.sh <publish folder> <version> <out/Kakudai.app>    (on macOS: sips, iconutil, codesign)
set -eu
publish=$1
version=$2
app=$3
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish"/. "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/Kakudai"

# The icon, every size iconutil wants, from the 512 px logo.
iconset=$(mktemp -d)/kakudai.iconset
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z $size $size "$root/docs/logo/logo-512.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  [ $double -le 512 ] && sips -z $double $double "$root/docs/logo/logo-512.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
cp "$root/docs/logo/logo-512.png" "$iconset/icon_512x512@2x.png"
iconutil -c icns "$iconset" -o "$app/Contents/Resources/kakudai.icns"

sed "s/@VERSION@/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"

codesign --force --deep --sign - "$app"
