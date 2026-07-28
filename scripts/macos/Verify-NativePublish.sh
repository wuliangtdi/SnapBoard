#!/bin/bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
    echo "Usage: Verify-NativePublish.sh <publish-directory> <osx-arm64|osx-x64>" >&2
    exit 2
fi

publish_dir="$1"
runtime="$2"
case "$runtime" in
    osx-arm64) expected_arch="arm64" ;;
    osx-x64) expected_arch="x86_64" ;;
    *)
        echo "Runtime must be osx-arm64 or osx-x64." >&2
        exit 2
        ;;
esac

main_entry="$publish_dir/SnapBoard.Desktop"
migrator_entry="$publish_dir/SnapBoard.StorageMigrator"

verify_mach_o() {
    local path="$1"
    local label="$2"
    if [[ ! -f "$path" ]]; then
        echo "$label is missing: $path" >&2
        exit 3
    fi

    local description
    description="$(file -b "$path")"
    if [[ "$description" != *"Mach-O 64-bit executable"* ||
          "$description" != *"$expected_arch"* ]]; then
        echo "$label is not a $runtime Mach-O executable: $description" >&2
        exit 3
    fi
}

verify_mach_o "$main_entry" "Desktop entry point"
verify_mach_o "$migrator_entry" "Storage migrator"

for forbidden in \
    libcoreclr.dylib \
    libhostfxr.dylib \
    SnapBoard.StorageMigrator.dll \
    SnapBoard.StorageMigrator.deps.json \
    SnapBoard.StorageMigrator.runtimeconfig.json; do
    if [[ -e "$publish_dir/$forbidden" ]]; then
        echo "Framework-dependent publish artifact found: $forbidden" >&2
        exit 3
    fi
done

set +e
"$migrator_entry" >/dev/null 2>&1
migrator_exit_code=$?
set -e
if [[ $migrator_exit_code -ne 4 ]]; then
    echo "Storage migrator must return 4 when no manifest is supplied; got $migrator_exit_code." >&2
    exit 3
fi

echo "DESKTOP_FILE=$(file -b "$main_entry")"
echo "MIGRATOR_FILE=$(file -b "$migrator_entry")"
echo "MIGRATOR_EMPTY_ARGS_EXIT=$migrator_exit_code"
