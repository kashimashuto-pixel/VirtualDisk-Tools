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
fat16_image="$fixture_dir/fat16-modified.raw"
fat32_image="$fixture_dir/fat32-modified.raw"
ntfs_image="$fixture_dir/ntfs-modified.raw"
exfat_image="$fixture_dir/exfat-modified.raw"
replacement="$fixture_dir/replacement.bin"
final_content="$fixture_dir/final-content.bin"
ext_mount="$fixture_dir/ext-verify-mount"
xfs_mount="$fixture_dir/xfs-verify-mount"
fat16_mount="$fixture_dir/fat16-verify-mount"
fat32_mount="$fixture_dir/fat32-verify-mount"
ntfs_mount="$fixture_dir/ntfs-verify-mount"
exfat_mount="$fixture_dir/exfat-verify-mount"

for path in "$ext_image" "$xfs_image" "$fat16_image" "$fat32_image" "$ntfs_image" "$exfat_image" "$replacement" "$final_content" "$fixture_dir/source.sha256"; do
    if [[ ! -f "$path" ]]; then
        echo "Required validation input not found: $path" >&2
        exit 1
    fi
done

for command_name in e2fsck xfs_repair fsck.fat ntfsfix fsck.exfat mount mountpoint umount cmp sha256sum; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Required command not found: $command_name" >&2
        exit 1
    fi
done

mkdir -p "$ext_mount" "$xfs_mount" "$fat16_mount" "$fat32_mount" "$ntfs_mount" "$exfat_mount"
cleanup() {
    if mountpoint -q "$ext_mount"; then
        umount "$ext_mount"
    fi
    if mountpoint -q "$xfs_mount"; then
        umount "$xfs_mount"
    fi
    if mountpoint -q "$fat16_mount"; then
        umount "$fat16_mount"
    fi
    if mountpoint -q "$fat32_mount"; then
        umount "$fat32_mount"
    fi
    if mountpoint -q "$ntfs_mount"; then
        umount "$ntfs_mount"
    fi
    if mountpoint -q "$exfat_mount"; then
        umount "$exfat_mount"
    fi
}
trap cleanup EXIT

(cd "$fixture_dir" && sha256sum -c source.sha256)
e2fsck -fn "$ext_image"
xfs_repair -n "$xfs_image"
fsck.fat -vn "$fat16_image"
fsck.fat -vn "$fat32_image"
ntfsfix -n "$ntfs_image"
fsck.exfat -n "$exfat_image"

mount -o loop,ro "$ext_image" "$ext_mount"
cmp "$final_content" "$ext_mount/Added batch.bin"
test ! -e "$ext_mount/payload.bin"
umount "$ext_mount"

mount -o loop,ro,norecovery "$xfs_image" "$xfs_mount"
cmp "$final_content" "$xfs_mount/Added batch.bin"
test ! -e "$xfs_mount/payload.bin"
umount "$xfs_mount"

mount -o loop,ro "$fat16_image" "$fat16_mount"
cmp "$final_content" "$fat16_mount/Added batch.bin"
test ! -e "$fat16_mount/payload.bin"
umount "$fat16_mount"

mount -o loop,ro "$fat32_image" "$fat32_mount"
cmp "$final_content" "$fat32_mount/Added batch.bin"
test ! -e "$fat32_mount/payload.bin"
umount "$fat32_mount"

mount -o loop,ro "$ntfs_image" "$ntfs_mount"
cmp "$final_content" "$ntfs_mount/Added batch.bin"
test ! -e "$ntfs_mount/payload.bin"
umount "$ntfs_mount"

mount -o loop,ro "$exfat_image" "$exfat_mount"
cmp "$final_content" "$exfat_mount/Added batch.bin"
test ! -e "$exfat_mount/payload.bin"
umount "$exfat_mount"

echo "ext4, XFS, FAT16, FAT32, NTFS, and exFAT batch-edit regression validation passed."
