#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "Usage: $0 <output-directory>" >&2
    exit 2
fi

if [[ $(id -u) -ne 0 ]]; then
    echo "This script must run as root because it mounts loop devices." >&2
    exit 1
fi

output_dir=$(realpath -m "$1")
mkdir -p "$output_dir"
ext_image="$output_dir/ext4-source.raw"
xfs_image="$output_dir/xfs-source.raw"
fat16_image="$output_dir/fat16-source.raw"
fat32_image="$output_dir/fat32-source.raw"
replacement="$output_dir/replacement.bin"
ext_mount="$output_dir/ext-mount"
xfs_mount="$output_dir/xfs-mount"
fat16_mount="$output_dir/fat16-mount"
fat32_mount="$output_dir/fat32-mount"

for command_name in truncate mkfs.ext4 e2fsck mkfs.xfs xfs_repair mkfs.fat fsck.fat mount mountpoint umount dd sha256sum; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Required command not found: $command_name" >&2
        exit 1
    fi
done

for path in "$ext_image" "$xfs_image" "$fat16_image" "$fat32_image" "$replacement"; do
    if [[ -e "$path" ]]; then
        echo "Refusing to overwrite existing fixture: $path" >&2
        exit 1
    fi
done

mkdir -p "$ext_mount" "$xfs_mount" "$fat16_mount" "$fat32_mount"
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
}
trap cleanup EXIT

dd if=/dev/urandom of="$replacement" bs=1M count=1 status=none

truncate -s 128M "$ext_image"
mkfs.ext4 -q -F -L VDT_WRITE_EXT4 "$ext_image"
mount -o loop "$ext_image" "$ext_mount"
dd if=/dev/zero of="$ext_mount/payload.bin" bs=1M count=1 conv=fsync status=none
umount "$ext_mount"
e2fsck -fn "$ext_image"

truncate -s 512M "$xfs_image"
mkfs.xfs -q -f -m crc=1,reflink=1,bigtime=1 -L VDT_WR_XFS "$xfs_image"
mount -o loop "$xfs_image" "$xfs_mount"
dd if=/dev/zero of="$xfs_mount/payload.bin" bs=1M count=1 conv=fsync status=none
umount "$xfs_mount"
xfs_repair -n "$xfs_image"

truncate -s 64M "$fat16_image"
mkfs.fat -F 16 -n VDT_FAT16 "$fat16_image"
mount -o loop "$fat16_image" "$fat16_mount"
dd if=/dev/zero of="$fat16_mount/payload.bin" bs=1M count=1 conv=fsync status=none
umount "$fat16_mount"
fsck.fat -vn "$fat16_image"

truncate -s 128M "$fat32_image"
mkfs.fat -F 32 -n VDT_FAT32 "$fat32_image"
mount -o loop "$fat32_image" "$fat32_mount"
dd if=/dev/zero of="$fat32_mount/payload.bin" bs=1M count=1 conv=fsync status=none
umount "$fat32_mount"
fsck.fat -vn "$fat32_image"

sha256sum "$ext_image" "$xfs_image" "$fat16_image" "$fat32_image" "$replacement" > "$output_dir/source.sha256"
echo "Write regression fixtures created in $output_dir"
