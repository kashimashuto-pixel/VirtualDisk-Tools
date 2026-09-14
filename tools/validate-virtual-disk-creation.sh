#!/usr/bin/env bash
set -euo pipefail

fixture_dir=${1:?usage: validate-virtual-disk-creation.sh FIXTURE_DIRECTORY}
fixture_dir=$(realpath "$fixture_dir")
content="$fixture_dir/created-content.bin"
mbr_source="$fixture_dir/mbr-raw.raw"
qcow_source="$fixture_dir/gpt-qcow2.qcow2"
mbr_edited="$fixture_dir/mbr-raw-edited.raw"
gpt_edited="$fixture_dir/gpt-qcow2-edited.raw"
mount_dir=$(mktemp -d /tmp/vdt-created-fs-mount.XXXXXX)
converted=
active_loop=

cleanup() {
    if mountpoint -q "$mount_dir"; then
        umount "$mount_dir"
    fi
    if [[ -n "$active_loop" ]]; then
        losetup -d "$active_loop" 2>/dev/null || true
    fi
    if [[ -n "$converted" ]]; then
        rm -f -- "$converted"
    fi
    rmdir "$mount_dir" 2>/dev/null || true
}
trap cleanup EXIT

for path in "$content" "$mbr_source" "$qcow_source" "$mbr_edited" "$gpt_edited"; do
    test -f "$path"
done

for command_name in sfdisk losetup udevadm xfs_repair e2fsck ntfsfix mount mountpoint umount cmp grep; do
    command -v "$command_name" >/dev/null
done

# qemu-img is deliberately optional. The application creates and reads QCOW2 with
# its own C# implementation; when available, qemu-img adds an independent check.
if command -v qemu-img >/dev/null; then
    converted=$(mktemp /tmp/vdt-created-qcow.XXXXXX.raw)
    qemu-img check "$qcow_source"
    qemu-img convert -O raw "$qcow_source" "$converted"
    sfdisk --verify "$converted"
else
    echo "qemu-img not installed; optional independent QCOW2 check skipped."
fi

sfdisk --verify "$mbr_source"
sfdisk --verify "$mbr_edited"
sfdisk --verify "$gpt_edited"

validate_partition() {
    local device=$1
    local file_system=$2
    local number=$3

    case "$file_system" in
        XFS)
            xfs_repair -n "$device"
            mount -o ro,norecovery "$device" "$mount_dir"
            ;;
        ext4)
            e2fsck -fn "$device"
            mount -o ro,noload "$device" "$mount_dir"
            ;;
        NTFS)
            ntfsfix -n "$device"
            mount -t ntfs-3g -o ro "$device" "$mount_dir"
            ;;
        *)
            echo "Unsupported validation file system: $file_system" >&2
            return 2
            ;;
    esac

    cmp "$content" "$mount_dir/FROM-CREATOR-${number}.BIN"
    cmp "$content" "$mount_dir/SEEDED-${number}.BIN"
    grep -q '^Created by Virtual Disk Explorer\.' "$mount_dir/VDT-README.txt"
    umount "$mount_dir"
}

validate_edited_raw() {
    local image=$1
    shift
    active_loop=$(losetup --find --show --partscan "$image")
    udevadm settle
    local number=1
    for file_system in "$@"; do
        local partition="${active_loop}p${number}"
        test -b "$partition"
        validate_partition "$partition" "$file_system" "$number"
        number=$((number + 1))
    done

    losetup -d "$active_loop"
    active_loop=
}

validate_edited_raw "$mbr_edited" ext4 NTFS
validate_edited_raw "$gpt_edited" XFS ext4 NTFS
echo "Independent virtual-disk creation validation passed."
