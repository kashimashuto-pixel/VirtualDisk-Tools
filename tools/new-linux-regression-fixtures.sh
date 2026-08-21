#!/usr/bin/env bash
set -euo pipefail

output_dir=${1:?usage: new-linux-regression-fixtures.sh OUTPUT_DIRECTORY}
xfs_image="$output_dir/xfs-bigtime.raw"
lzop_image="$output_dir/large-xfs.dd.lzo"
mount_dir=$(mktemp -d)
loop_device=""
generation_started=0

cleanup() {
    local status=$?
    trap - EXIT
    set +e
    if mountpoint -q "$mount_dir"; then
        umount "$mount_dir"
    fi
    if [[ -n "$loop_device" ]]; then
        losetup -d "$loop_device"
    fi
    rmdir "$mount_dir"
    if [[ $status -ne 0 && $generation_started -eq 1 ]]; then
        rm -f -- "$xfs_image" "$lzop_image"
    fi
    exit "$status"
}
trap cleanup EXIT

for command_name in truncate sfdisk losetup mkfs.xfs mount mountpoint umount lzop sha256sum; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Missing required command: $command_name" >&2
        echo "Install xfsprogs, lzop, and util-linux in the WSL distribution." >&2
        exit 3
    fi
done

mkdir -p "$output_dir"
if [[ -e "$xfs_image" || -e "$lzop_image" ]]; then
    echo "Refusing to overwrite an existing fixture in: $output_dir" >&2
    exit 2
fi
generation_started=1

truncate -s 768M "$xfs_image"
printf 'label: gpt\nstart=2048, type=L\n' | sfdisk "$xfs_image"

loop_device=$(losetup --find --show --partscan "$xfs_image")
partition="${loop_device}p1"
for _ in {1..20}; do
    [[ -b "$partition" ]] && break
    sleep 0.1
done
if [[ ! -b "$partition" ]]; then
    echo "The XFS fixture partition device was not created: $partition" >&2
    exit 4
fi

mkfs.xfs -f -m crc=1,bigtime=1 -L VDT_BIGTIME "$partition"
mount "$partition" "$mount_dir"
printf 'XFS bigtime fixture\n' > "$mount_dir/bigtime.txt"
touch -d '2045-01-02 03:04:05 UTC' "$mount_dir/bigtime.txt"
sync

xfs_info "$mount_dir"
stat --format='file_size=%s file_mtime=%y' "$mount_dir/bigtime.txt"
sha256sum "$mount_dir/bigtime.txt"

umount "$mount_dir"
losetup -d "$loop_device"
loop_device=""

lzop -9 --output="$lzop_image" "$xfs_image"
sha256sum "$xfs_image" "$lzop_image"
ls -lh "$xfs_image" "$lzop_image"
