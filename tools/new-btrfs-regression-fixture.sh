#!/usr/bin/env bash
set -euo pipefail

output_file=${1:?usage: new-btrfs-regression-fixture.sh OUTPUT_FILE SIZE_MIB}
size_mib=${2:?usage: new-btrfs-regression-fixture.sh OUTPUT_FILE SIZE_MIB}
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
        rm -f -- "$output_file"
    fi
    exit "$status"
}
trap cleanup EXIT

for command_name in truncate sfdisk losetup mkfs.btrfs btrfs mount mountpoint umount sha256sum dd; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Missing required command: $command_name" >&2
        echo "Install btrfs-progs and util-linux in the WSL distribution." >&2
        exit 3
    fi
done

if [[ -e "$output_file" ]]; then
    echo "Refusing to overwrite an existing fixture: $output_file" >&2
    exit 2
fi
generation_started=1

truncate -s "${size_mib}M" "$output_file"
printf 'label: gpt\nstart=2048, type=L\n' | sfdisk "$output_file"

loop_device=$(losetup --find --show --partscan "$output_file")
partition="${loop_device}p1"
for _ in {1..20}; do
    [[ -b "$partition" ]] && break
    sleep 0.1
done
if [[ ! -b "$partition" ]]; then
    echo "The Btrfs fixture partition device was not created: $partition" >&2
    exit 4
fi

mkfs.btrfs -f -m single -d single -L VDT_BTRFS "$partition"
mount -o compress=no "$partition" "$mount_dir"
mkdir "$mount_dir/nested"
printf 'Hello from Btrfs\n' > "$mount_dir/hello.txt"
dd if=/dev/zero of="$mount_dir/nested/regular.bin" bs=131072 count=1 status=none
touch "$mount_dir/compressed-zlib.bin"
btrfs property set "$mount_dir/compressed-zlib.bin" compression zlib
dd if=/dev/zero of="$mount_dir/compressed-zlib.bin" bs=131072 count=2 status=none
touch "$mount_dir/compressed-lzo.bin"
btrfs property set "$mount_dir/compressed-lzo.bin" compression lzo
dd if=/dev/zero of="$mount_dir/compressed-lzo.bin" bs=131072 count=2 status=none
touch "$mount_dir/compressed-inline-lzo.bin"
btrfs property set "$mount_dir/compressed-inline-lzo.bin" compression lzo
dd if=/dev/zero of="$mount_dir/compressed-inline-lzo.bin" bs=1024 count=1 status=none
truncate -s 1048576 "$mount_dir/sparse.bin"
printf 'sparse-tail\n' >> "$mount_dir/sparse.bin"
touch -d '2046-02-03 04:05:06 UTC' "$mount_dir/hello.txt"
sync

stat --format='hello_size=%s hello_mtime=%y' "$mount_dir/hello.txt"
stat --format='%n size=%s' "$mount_dir/nested/regular.bin" "$mount_dir/sparse.bin"
stat --format='%n size=%s' "$mount_dir/compressed-zlib.bin"
stat --format='%n size=%s' "$mount_dir/compressed-lzo.bin"
stat --format='%n size=%s' "$mount_dir/compressed-inline-lzo.bin"
sha256sum "$mount_dir/hello.txt" "$mount_dir/nested/regular.bin" "$mount_dir/compressed-zlib.bin" "$mount_dir/compressed-lzo.bin" "$mount_dir/compressed-inline-lzo.bin" "$mount_dir/sparse.bin"

umount "$mount_dir"
btrfs check --readonly "$partition"
losetup -d "$loop_device"
loop_device=""
sha256sum "$output_file"
