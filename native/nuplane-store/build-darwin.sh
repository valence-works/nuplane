#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source_file="$script_dir/openat-create.c"
script_file="$script_dir/build-darwin.sh"
verifier_file="$script_dir/verify-darwin.py"
library_file="libnuplane_store_native.dylib"
manifest_file="nuplane-store-native.manifest.json"

verify_output() {
  python3 "$verifier_file" verify \
    "$source_file" "$script_file" "$verifier_file" "$1/$library_file" "$1/$manifest_file"
}

if [[ "${1:-}" == "--verify" ]]; then
  if [[ $# -ne 2 ]]; then
    echo "Usage: $0 --verify OUTPUT_DIR" >&2
    exit 2
  fi
  verify_output "$2"
  exit 0
fi

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 OUTPUT_DIR | --verify OUTPUT_DIR" >&2
  exit 2
fi
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Building the universal dylib requires macOS with Xcode command-line tools." >&2
  exit 1
fi
command -v xcrun >/dev/null || { echo "xcrun is required to build the dylib." >&2; exit 1; }
command -v python3 >/dev/null || { echo "python3 is required to write and verify the manifest." >&2; exit 1; }

output_dir="$1"
mkdir -p -- "$output_dir"
output_dir="$(cd -- "$output_dir" && pwd)"
temporary_dir="$(mktemp -d "$output_dir/.nuplane-store-native.XXXXXX")"
trap 'rm -rf -- "$temporary_dir"' EXIT
sdk_path="$(xcrun --sdk macosx --show-sdk-path)"
initial_hashes="$(python3 "$verifier_file" fingerprint "$source_file" "$script_file")"
source_hash_before="${initial_hashes%% *}"
script_hash_before="${initial_hashes#* }"

for architecture in arm64 x86_64; do
  xcrun --sdk macosx clang \
    -isysroot "$sdk_path" \
    -arch "$architecture" \
    -mmacosx-version-min=11.0 \
    -std=c11 -O2 -Wall -Wextra -Werror -fPIC -fvisibility=hidden -dynamiclib \
    -Wl,-install_name,@rpath/libnuplane_store_native.dylib \
    "$source_file" \
    -o "$temporary_dir/libnuplane_store_native-$architecture.dylib"
done

xcrun lipo -create \
  "$temporary_dir/libnuplane_store_native-arm64.dylib" \
  "$temporary_dir/libnuplane_store_native-x86_64.dylib" \
  -output "$temporary_dir/$library_file"

python3 "$verifier_file" write \
  "$source_file" "$script_file" "$verifier_file" \
  "$temporary_dir/$library_file" "$temporary_dir/$manifest_file" \
  "$source_hash_before" "$script_hash_before"
verify_output "$temporary_dir"

# Both artifacts are built and checked on the same filesystem as their final
# names. Concurrent identical invocations publish byte-identical files.
mv -f "$temporary_dir/$library_file" "$output_dir/$library_file"
mv -f "$temporary_dir/$manifest_file" "$output_dir/$manifest_file"
verify_output "$output_dir"
