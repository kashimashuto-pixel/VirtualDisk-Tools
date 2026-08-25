#!/usr/bin/env bash
set -euo pipefail

size_mib=${1:?usage: new-btrfs-raid10-regression-fixture.sh SIZE_MIB OUTPUT1 OUTPUT2 OUTPUT3 OUTPUT4}
shift
outputs=("$@")
mount_dir=$(mktemp -d)
loops=()
partitions=()
generation_started=0

cleanup() {
    local status=$?
    trap - EXIT
    set +e
    if mountpoint -q "$mount_dir"; then
        umount "$mount_dir"
    fi
    for loop_device in "${loops[@]}"; do
        losetup -d "$loop_device"
    done
    rmdir "$mount_dir"
    if [[ $status -ne 0 && $generation_started -eq 1 ]]; then
        rm -f -- "${outputs[@]}"
    fi
    exit "$status"
}
trap cleanup EXIT

if [[ ${#outputs[@]} -ne 4 ]]; then
    echo "RAID10 fixture requires exactly four output files." >&2
    exit 2
fi
for command_name in truncate sfdisk losetup mkfs.btrfs btrfs mount mountpoint umount sha256sum dd; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Missing required command: $command_name" >&2
        echo "Install btrfs-progs and util-linux in the WSL distribution." >&2
        exit 3
    fi
done
for output_file in "${outputs[@]}"; do
    if [[ -e "$output_file" ]]; then
        echo "Refusing to overwrite an existing fixture: $output_file" >&2
        exit 2
    fi
done
generation_started=1

for output_file in "${outputs[@]}"; do
    truncate -s "${size_mib}M" "$output_file"
    printf 'label: gpt\nstart=2048, type=L\n' | sfdisk "$output_file"
    loop_device=$(losetup --find --show --partscan "$output_file")
    loops+=("$loop_device")
    partitions+=("${loop_device}p1")
done

for _ in {1..20}; do
    all_ready=1
    for partition in "${partitions[@]}"; do
        [[ -b "$partition" ]] || all_ready=0
    done
    [[ $all_ready -eq 1 ]] && break
    sleep 0.1
done
for partition in "${partitions[@]}"; do
    if [[ ! -b "$partition" ]]; then
        echo "Btrfs fixture partition device was not created: $partition" >&2
        exit 4
    fi
done

mkfs.btrfs -f -m raid10 -d raid10 -L VDT_RAID10 "${partitions[@]}"
btrfs device scan "${partitions[@]}"
mount_options="compress=no"
for partition in "${partitions[@]:1}"; do
    mount_options="device=$partition,$mount_options"
done
mount -o "$mount_options" "${partitions[0]}" "$mount_dir"
printf 'Hello from Btrfs raid10\n' > "$mount_dir/hello.txt"
dd if=/dev/zero of="$mount_dir/regular.bin" bs=131072 count=2 status=none
touch "$mount_dir/compressed-lzo.bin"
btrfs property set "$mount_dir/compressed-lzo.bin" compression lzo
dd if=/dev/zero of="$mount_dir/compressed-lzo.bin" bs=131072 count=2 status=none
touch "$mount_dir/compressed-zstd.bin"
btrfs property set "$mount_dir/compressed-zstd.bin" compression zstd
dd if=/dev/zero of="$mount_dir/compressed-zstd.bin" bs=131072 count=2 status=none
btrfs subvolume create "$mount_dir/subvol"
printf 'inside raid10 subvolume\n' > "$mount_dir/subvol/subvolume.txt"
sync

btrfs filesystem usage "$mount_dir"
sha256sum "$mount_dir/hello.txt" "$mount_dir/regular.bin" \
    "$mount_dir/compressed-lzo.bin" "$mount_dir/compressed-zstd.bin" \
    "$mount_dir/subvol/subvolume.txt"

umount "$mount_dir"
btrfs check --readonly "${partitions[0]}"
for loop_device in "${loops[@]}"; do
    losetup -d "$loop_device"
done
loops=()
sha256sum "${outputs[@]}"
