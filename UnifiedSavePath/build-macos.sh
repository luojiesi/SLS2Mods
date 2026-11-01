#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -m)" != "arm64" ]]; then
  echo "This release script supports native Apple Silicon (arm64) only." >&2
  exit 1
fi

if command -v dotnet >/dev/null 2>&1; then
  dotnet_bin="$(command -v dotnet)"
elif [[ -x /opt/homebrew/opt/dotnet@9/bin/dotnet ]]; then
  dotnet_bin="/opt/homebrew/opt/dotnet@9/bin/dotnet"
else
  echo "The .NET 9 SDK is required. Install it with: brew install dotnet@9" >&2
  exit 1
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_dir="$(cd "$script_dir/.." && pwd)"
game_dir="${1:-${STS2_GAME_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}}"
game_data_dir="$game_dir/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
configuration="Release"
version="1.0.1"
package_name="UnifiedSavePath-$version-macos-arm64"
dist_dir="$repo_dir/dist"
package_dir="$dist_dir/$package_name/UnifiedSavePath"

for assembly in sts2.dll 0Harmony.dll; do
  if [[ ! -f "$game_data_dir/$assembly" ]]; then
    echo "Missing game assembly: $game_data_dir/$assembly" >&2
    exit 1
  fi
done

"$dotnet_bin" build "$script_dir/UnifiedSavePath.csproj" \
  --configuration "$configuration" \
  -p:STS2GameDir="$game_dir"

rm -rf "$dist_dir/$package_name" "$dist_dir/$package_name.zip"
mkdir -p "$package_dir"
cp "$script_dir/bin/$configuration/net9.0/UnifiedSavePath.dll" "$package_dir/"
cp "$script_dir/UnifiedSavePath.json" "$package_dir/"

(
  cd "$dist_dir"
  zip -X -q -r "$package_name.zip" "$package_name"
)

echo "Package created: $dist_dir/$package_name.zip"
