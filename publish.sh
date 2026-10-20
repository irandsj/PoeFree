#!/usr/bin/env sh
# Builds release packages into ./publish/<runtime> (default: linux-x64)
set -e
RID="${1:-linux-x64}"
OUT="$(dirname "$0")/publish/$RID"
rm -rf "$OUT"
for p in src/PoeFree.App/PoeFree.App.csproj src/PoeFree.Cli/PoeFree.Cli.csproj; do
  dotnet publish "$p" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -p:EnableCompressionInSingleFile=true -o "$OUT"
done
cp LICENSE README.md "$OUT"
echo "Done: $OUT (copy liboo2core next to the binaries: it is not redistributed)"
