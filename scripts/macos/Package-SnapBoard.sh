#!/bin/bash
set -euo pipefail
export COPYFILE_DISABLE=1

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
configuration="Release"
runtime=""
version="0.0.0-dev"
build_number="1"
output_root="$repo_root/artifacts/macos"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --configuration)
            configuration="$2"
            shift 2
            ;;
        --runtime)
            runtime="$2"
            shift 2
            ;;
        --version)
            version="$2"
            shift 2
            ;;
        --build-number)
            build_number="$2"
            shift 2
            ;;
        --output)
            output_root="$2"
            shift 2
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 2
            ;;
    esac
done

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "macOS packaging must run on macOS." >&2
    exit 2
fi

host_arch="$(uname -m)"
if [[ -z "$runtime" ]]; then
    case "$host_arch" in
        arm64) runtime="osx-arm64" ;;
        x86_64) runtime="osx-x64" ;;
        *)
            echo "Unsupported macOS architecture: $host_arch" >&2
            exit 2
            ;;
    esac
fi

if [[ "$runtime" != "osx-arm64" && "$runtime" != "osx-x64" ]]; then
    echo "Runtime must be osx-arm64 or osx-x64." >&2
    exit 2
fi

expected_runtime="osx-arm64"
if [[ "$host_arch" == "x86_64" ]]; then
    expected_runtime="osx-x64"
fi
if [[ "$runtime" != "$expected_runtime" ]]; then
    echo "Native AOT package validation requires matching host and target architectures." >&2
    exit 2
fi

if [[ ! "$version" =~ ^[0-9]+([.][0-9]+){1,2}([+-][A-Za-z0-9.-]+)?$ ]]; then
    echo "Version must contain two or three numeric parts with an optional prerelease suffix." >&2
    exit 2
fi
bundle_version="${version%%[-+]*}"
if [[ ! "$build_number" =~ ^[0-9]+$ ]]; then
    echo "Build number must be numeric." >&2
    exit 2
fi

case "$output_root" in
    ""|"/"|"$repo_root")
        echo "Refusing unsafe output directory: $output_root" >&2
        exit 2
        ;;
esac

publish_dir="$output_root/publish-$runtime"
bundle_dir="$output_root/SnapBoard.app"
staging_dir="$output_root/dmg-root"
dmg_path="$output_root/SnapBoard-$runtime.dmg"
unsigned_pkg="$output_root/SnapBoard-$runtime-unsigned.pkg"
pkg_path="$output_root/SnapBoard-$runtime.pkg"
checksum_path="$output_root/SnapBoard-$runtime.sha256"
legacy_checksum_path="$output_root/SHA256SUMS"

rm -rf "$publish_dir" "$bundle_dir" "$staging_dir"
rm -f "$dmg_path" "$unsigned_pkg" "$pkg_path" "$checksum_path" "$legacy_checksum_path"
mkdir -p "$publish_dir" "$bundle_dir/Contents/MacOS" "$bundle_dir/Contents/Resources"

cd "$repo_root"
dotnet restore src/SnapBoard.Desktop/SnapBoard.Desktop.csproj \
    --locked-mode
dotnet publish src/SnapBoard.Desktop/SnapBoard.Desktop.csproj \
    --configuration "$configuration" \
    --runtime "$runtime" \
    --self-contained true \
    --no-restore \
    -p:PublishAot=true \
    -p:Version="$version" \
    -o "$publish_dir"

published_entry="$publish_dir/SnapBoard.Desktop"
published_migrator="$publish_dir/SnapBoard.StorageMigrator"
"$script_dir/Verify-NativePublish.sh" "$publish_dir" "$runtime"

cp "$published_entry" "$bundle_dir/Contents/MacOS/SnapBoard"
cp "$published_migrator" "$bundle_dir/Contents/MacOS/SnapBoard.StorageMigrator"
while IFS= read -r native_library; do
    if [[ "$native_library" != "$published_entry" &&
          "$native_library" != "$published_migrator" ]] &&
        file "$native_library" | grep -q "Mach-O"; then
        cp "$native_library" "$bundle_dir/Contents/MacOS/"
    fi
done < <(find "$publish_dir" -maxdepth 1 -type f -print)
cp "$repo_root/packaging/macos/SnapBoard.icns" "$bundle_dir/Contents/Resources/SnapBoard.icns"
cp "$repo_root/src/SnapBoard.Platform.MacOS/Assets/snapboard-app-icon.png" \
    "$bundle_dir/Contents/Resources/snapboard-app-icon.png"
cp "$repo_root/src/SnapBoard.Platform.MacOS/Assets/snapboard-menubar-template.png" \
    "$bundle_dir/Contents/Resources/snapboard-menubar-template.png"
cp "$repo_root/src/SnapBoard.Platform.MacOS/Assets/snapboard-menubar-template@2x.png" \
    "$bundle_dir/Contents/Resources/snapboard-menubar-template@2x.png"

sed \
    -e "s/@VERSION@/$bundle_version/g" \
    -e "s/@BUILD_NUMBER@/$build_number/g" \
    "$repo_root/packaging/macos/Info.plist.in" > "$bundle_dir/Contents/Info.plist"
plutil -lint "$bundle_dir/Contents/Info.plist"
xattr -cr "$bundle_dir"

sign_identity="${SNAPBOARD_SIGN_IDENTITY:--}"
installer_identity="${SNAPBOARD_INSTALLER_IDENTITY:-}"
if [[ "$sign_identity" != "-" && -z "$installer_identity" ]]; then
    echo "A Developer ID Installer identity is required for a signed PKG release." >&2
    exit 4
fi
if [[ "$sign_identity" == "-" && -n "$installer_identity" ]]; then
    echo "A PKG cannot be release-signed while the application uses ad-hoc signing." >&2
    exit 4
fi

sign_args=(--force --sign "$sign_identity" --options runtime)
entitlements_path="$repo_root/packaging/macos/SnapBoard.entitlements"
if [[ "$sign_identity" != "-" ]]; then
    sign_args+=(--timestamp)
else
    # ad-hoc 签名没有 Team ID；仅本机验证包关闭库验证，Developer ID 发布包保持开启。
    entitlements_path="$repo_root/packaging/macos/SnapBoard.adhoc.entitlements"
fi

while IFS= read -r published_file; do
    if file "$published_file" | grep -q "Mach-O"; then
        chmod 0755 "$published_file"
        if [[ "$published_file" != "$bundle_dir/Contents/MacOS/SnapBoard" ]]; then
            codesign "${sign_args[@]}" "$published_file"
        fi
    else
        chmod 0644 "$published_file"
    fi
done < <(find "$bundle_dir/Contents/MacOS" -type f -print)

codesign "${sign_args[@]}" \
    --entitlements "$entitlements_path" \
    "$bundle_dir"
codesign --verify --strict --verbose=2 \
    "$bundle_dir/Contents/MacOS/SnapBoard.StorageMigrator"
codesign --verify --deep --strict --verbose=2 "$bundle_dir"

mkdir -p "$staging_dir"
ditto "$bundle_dir" "$staging_dir/SnapBoard.app"
ln -s /Applications "$staging_dir/Applications"
hdiutil create \
    -volname "SnapBoard" \
    -srcfolder "$staging_dir" \
    -ov \
    -format UDZO \
    "$dmg_path"

pkgbuild \
    --component "$bundle_dir" \
    --install-location /Applications \
    "$unsigned_pkg"
if [[ -n "$installer_identity" ]]; then
    productsign \
        --sign "$installer_identity" \
        --timestamp \
        "$unsigned_pkg" \
        "$pkg_path"
    rm -f "$unsigned_pkg"
    pkgutil --check-signature "$pkg_path"
    echo "PKG_SIGNING_STATUS=developer-id-installer"
else
    mv "$unsigned_pkg" "$pkg_path"
    echo "PKG_SIGNING_STATUS=unsigned"
fi

if [[ "$sign_identity" == "-" ]]; then
    echo "SIGNING_STATUS=adhoc"
else
    codesign --force --sign "$sign_identity" --timestamp "$dmg_path"
    codesign --verify --verbose=2 "$dmg_path"
    echo "SIGNING_STATUS=developer-id"
fi

notary_profile="${SNAPBOARD_NOTARY_PROFILE:-}"
if [[ -n "$notary_profile" && "$sign_identity" != "-" ]]; then
    xcrun notarytool submit "$dmg_path" \
        --keychain-profile "$notary_profile" \
        --wait
    xcrun stapler staple "$dmg_path"
    if [[ -n "$installer_identity" ]]; then
        xcrun notarytool submit "$pkg_path" \
            --keychain-profile "$notary_profile" \
            --wait
        xcrun stapler staple "$pkg_path"
    fi
    xcrun stapler validate "$dmg_path"
    xcrun stapler validate "$pkg_path"
    spctl --assess --type open --context context:primary-signature --verbose=2 "$dmg_path"
    spctl --assess --type install --verbose=2 "$pkg_path"
    echo "NOTARIZATION_STATUS=accepted"
else
    echo "NOTARIZATION_STATUS=skipped"
fi

(
    cd "$output_root"
    shasum -a 256 "$(basename "$dmg_path")" "$(basename "$pkg_path")"
) > "$checksum_path"
echo "BUNDLE_PATH=$bundle_dir"
echo "DMG_PATH=$dmg_path"
echo "PKG_PATH=$pkg_path"
echo "CHECKSUM_PATH=$checksum_path"
echo "RUNTIME=$runtime"
