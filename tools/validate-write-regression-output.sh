#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "Usage: $0 <fixture-directory>" >&2
    exit 2
fi

if [[ $(id -u) -ne 0 ]]; then
    echo "This script must run as root because it mounts loop devices." >&2
    exit 1
fi

fixture_dir=$(realpath "$1")
ext_image="$fixture_dir/ext4-modified.raw"
xfs_image="$fixture_dir/xfs-modified.raw"
replacement="$fixture_dir/replacement.bin"
ext_mount="$fixture_dir/ext-verify-mount"
xfs_mount="$fixture_dir/xfs-verify-mount"

for path in "$ext_image" "$xfs_image" "$replacement" "$fixture_dir/source.sha256"; do
    if [[ ! -f "$path" ]]; then
        echo "Required validation input not found: $path" >&2
        exit 1
    fi
done

for command_name in e2fsck xfs_repair mount mountpoint umount cmp sha256sum; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Required command not found: $command_name" >&2
        exit 1
    fi
done

mkdir -p "$ext_mount" "$xfs_mount"
cleanup() {
    if mountpoint -q "$ext_mount"; then
        umount "$ext_mount"
    fi
    if mountpoint -q "$xfs_mount"; then
        umount "$xfs_mount"
    fi
}
trap cleanup EXIT

(cd "$fixture_dir" && sha256sum -c source.sha256)
e2fsck -fn "$ext_image"
xfs_repair -n "$xfs_image"

mount -o loop,ro "$ext_image" "$ext_mount"
cmp "$replacement" "$ext_mount/payload.bin"
umount "$ext_mount"

mount -o loop,ro,norecovery "$xfs_image" "$xfs_mount"
cmp "$replacement" "$xfs_mount/payload.bin"
umount "$xfs_mount"

echo "ext4 and XFS write regression validation passed."
