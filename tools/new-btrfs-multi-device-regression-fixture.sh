#!/usr/bin/env bash
set -euo pipefail

first_output=${1:?usage: new-btrfs-multi-device-regression-fixture.sh FIRST_OUTPUT SECOND_OUTPUT SIZE_MIB PROFILE}
second_output=${2:?usage: new-btrfs-multi-device-regression-fixture.sh FIRST_OUTPUT SECOND_OUTPUT SIZE_MIB PROFILE}
size_mib=${3:?usage: new-btrfs-multi-device-regression-fixture.sh FIRST_OUTPUT SECOND_OUTPUT SIZE_MIB PROFILE}
profile=${4:?usage: new-btrfs-multi-device-regression-fixture.sh FIRST_OUTPUT SECOND_OUTPUT SIZE_MIB PROFILE}
mount_dir=$(mktemp -d)
first_loop=""
second_loop=""
generation_started=0

cleanup() {
    local status=$?
    trap - EXIT
    set +e
    if mountpoint -q "$mount_dir"; then
        umount "$mount_dir"
    fi
    if [[ -n "$first_loop" ]]; then
        losetup -d "$first_loop"
    fi
    if [[ -n "$second_loop" ]]; then
        losetup -d "$second_loop"
    fi
    rmdir "$mount_dir"
    if [[ $status -ne 0 && $generation_started -eq 1 ]]; then
        rm -f -- "$first_output" "$second_output"
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

if [[ "$profile" != "single" && "$profile" != "raid1" ]]; then
    echo "Unsupported Btrfs profile: $profile" >&2
    exit 2
fi
if [[ "$first_output" == "$second_output" ]]; then
    echo "Output files must be different." >&2
    exit 2
fi
if [[ -e "$first_output" || -e "$second_output" ]]; then
    echo "Refusing to overwrite an existing fixture." >&2
    exit 2
fi
generation_started=1

for output_file in "$first_output" "$second_output"; do
    truncate -s "${size_mib}M" "$output_file"
    printf 'label: gpt\nstart=2048, type=L\n' | sfdisk "$output_file"
done

first_loop=$(losetup --find --show --partscan "$first_output")
second_loop=$(losetup --find --show --partscan "$second_output")
first_partition="${first_loop}p1"
second_partition="${second_loop}p1"
for _ in {1..20}; do
    [[ -b "$first_partition" && -b "$second_partition" ]] && break
    sleep 0.1
done
if [[ ! -b "$first_partition" || ! -b "$second_partition" ]]; then
    echo "Btrfs fixture partition devices were not created." >&2
    exit 4
fi

mkfs.btrfs -f -m "$profile" -d "$profile" -L "VDT_BTRFS_${profile^^}" \
    "$first_partition" "$second_partition"
btrfs device scan "$first_partition" "$second_partition"
mount -o "device=$second_partition,compress=no" "$first_partition" "$mount_dir"
printf 'Hello from multi-device Btrfs\n' > "$mount_dir/hello.txt"
dd if=/dev/zero of="$mount_dir/regular.bin" bs=131072 count=1 status=none
touch "$mount_dir/compressed-zlib.bin"
btrfs property set "$mount_dir/compressed-zlib.bin" compression zlib
dd if=/dev/zero of="$mount_dir/compressed-zlib.bin" bs=131072 count=2 status=none
touch "$mount_dir/compressed-lzo.bin"
btrfs property set "$mount_dir/compressed-lzo.bin" compression lzo
dd if=/dev/zero of="$mount_dir/compressed-lzo.bin" bs=131072 count=2 status=none
touch "$mount_dir/compressed-zstd.bin"
btrfs property set "$mount_dir/compressed-zstd.bin" compression zstd
dd if=/dev/zero of="$mount_dir/compressed-zstd.bin" bs=131072 count=2 status=none
btrfs subvolume create "$mount_dir/subvol"
printf 'inside multi-device subvolume\n' > "$mount_dir/subvol/subvolume.txt"
sync

btrfs filesystem usage "$mount_dir"
sha256sum "$mount_dir/hello.txt" "$mount_dir/regular.bin" \
    "$mount_dir/compressed-zlib.bin" "$mount_dir/compressed-lzo.bin" \
    "$mount_dir/compressed-zstd.bin" "$mount_dir/subvol/subvolume.txt"

umount "$mount_dir"
btrfs check --readonly "$first_partition"
losetup -d "$first_loop"
first_loop=""
losetup -d "$second_loop"
second_loop=""
sha256sum "$first_output" "$second_output"
