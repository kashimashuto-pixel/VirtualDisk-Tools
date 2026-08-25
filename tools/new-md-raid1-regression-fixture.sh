#!/usr/bin/env bash
set -euo pipefail

first_output=${1:?usage: new-md-raid1-regression-fixture.sh FIRST_OUTPUT SECOND_OUTPUT SIZE_MIB}
second_output=${2:?usage: new-md-raid1-regression-fixture.sh FIRST_OUTPUT SECOND_OUTPUT SIZE_MIB}
size_mib=${3:?usage: new-md-raid1-regression-fixture.sh FIRST_OUTPUT SECOND_OUTPUT SIZE_MIB}
mount_dir=$(mktemp -d)
first_loop=""
second_loop=""
md_device="/dev/md/vdt-mdraid1-$$"
generation_started=0

cleanup() {
    local status=$?
    trap - EXIT
    set +e
    if mountpoint -q "$mount_dir"; then
        umount "$mount_dir"
    fi
    if [[ -e "$md_device" ]]; then
        mdadm --stop "$md_device"
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

for command_name in truncate sfdisk losetup mdadm mkfs.ext4 e2fsck mount mountpoint umount sha256sum; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Missing required command: $command_name" >&2
        echo "Install mdadm, e2fsprogs, and util-linux in the WSL distribution." >&2
        exit 3
    fi
done
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
    printf 'label: gpt\nstart=2048, type=A19D880F-05FC-4D3B-A006-743F0F84911E\n' | sfdisk "$output_file"
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
    echo "Linux md fixture partition devices were not created." >&2
    exit 4
fi

mkdir -p /dev/md
mdadm --create "$md_device" --run --metadata=1.2 --level=1 --raid-devices=2 \
    --name=VDT_MDRAID1 "$first_partition" "$second_partition"
mdadm --wait "$md_device"
mkfs.ext4 -F -L VDT_MDRAID1 "$md_device"
mount -o ro,noload "$md_device" "$mount_dir"
umount "$mount_dir"
mount "$md_device" "$mount_dir"
printf 'Hello from Linux md RAID1\n' > "$mount_dir/hello.txt"
mkdir "$mount_dir/nested"
printf 'inside md RAID1\n' > "$mount_dir/nested/readme.txt"
sync
sha256sum "$mount_dir/hello.txt" "$mount_dir/nested/readme.txt"
umount "$mount_dir"
e2fsck -fn "$md_device"
mdadm --detail --export "$md_device"
mdadm --stop "$md_device"
first_loop_partition="$first_partition"
second_loop_partition="$second_partition"
mdadm --examine --export "$first_loop_partition"
mdadm --examine --export "$second_loop_partition"
losetup -d "$first_loop"
first_loop=""
losetup -d "$second_loop"
second_loop=""
sha256sum "$first_output" "$second_output"
