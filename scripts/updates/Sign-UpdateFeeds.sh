#!/bin/bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
public_key="$repo_root/packaging/updates/update-signing-public.pem"

if [[ $# -ne 2 ]]; then
    echo "Usage: $0 <artifact-directory> <private-key.pem>" >&2
    exit 2
fi

artifact_directory="$1"
private_key="$2"
if [[ ! -d "$artifact_directory" ]]; then
    echo "Artifact directory does not exist: $artifact_directory" >&2
    exit 2
fi
if [[ ! -f "$private_key" ]]; then
    echo "Update signing private key does not exist." >&2
    exit 2
fi

if [[ "$(uname -s)" == "Darwin" ]]; then
    private_mode="$(stat -f '%Lp' "$private_key")"
else
    private_mode="$(stat -c '%a' "$private_key")"
fi
if (( (8#$private_mode & 8#077) != 0 )); then
    echo "Update signing private key must not be readable by group or other users." >&2
    exit 3
fi

private_fingerprint="$(
    openssl pkey -in "$private_key" -pubout -outform DER 2>/dev/null |
        openssl dgst -sha256
)"
public_fingerprint="$(
    openssl pkey -pubin -in "$public_key" -outform DER 2>/dev/null |
        openssl dgst -sha256
)"
if [[ "$private_fingerprint" != "$public_fingerprint" ]]; then
    echo "Update signing private key does not match the committed public key." >&2
    exit 3
fi

feed_count=0
while IFS= read -r feed; do
    feed_name="$(basename "$feed")"
    if [[ ! "$feed_name" =~ ^releases\.(win|osx|linux)-(x64|arm64)-(stable|beta)\.json$ ]]; then
        echo "Refusing unexpected update feed name: $feed_name" >&2
        exit 3
    fi

    temporary_signature="$feed.sig.tmp"
    openssl dgst -sha256 -sign "$private_key" -out "$temporary_signature" "$feed"
    openssl dgst -sha256 -verify "$public_key" \
        -signature "$temporary_signature" "$feed" >/dev/null
    mv "$temporary_signature" "$feed.sig"
    chmod 0644 "$feed.sig"
    feed_count=$((feed_count + 1))
done < <(find "$artifact_directory" -type f -name 'releases.*.json' -print | LC_ALL=C sort)

if (( feed_count == 0 )); then
    echo "No update feeds were found to sign." >&2
    exit 3
fi

echo "Signed and verified $feed_count update feed(s)."
