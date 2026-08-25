#!/usr/bin/env bash
set -euo pipefail

size_mib=${1:?usage: new-md-raid10-regression-fixture.sh SIZE_MIB OUTPUT1 OUTPUT2 OUTPUT3 OUTPUT4}
shift
outputs=("$@")
mount_dir=$(mktemp -d)
loops=()
partitions=()
md_device="/dev/md/vdt-mdraid10-$$"
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
for command_name in truncate sfdisk losetup mdadm mkfs.ext4 e2fsck mount mountpoint umount sha256sum python3; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Missing required command: $command_name" >&2
        echo "Install mdadm, e2fsprogs, python3, and util-linux in the WSL distribution." >&2
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
    printf 'label: gpt\nstart=2048, type=A19D880F-05FC-4D3B-A006-743F0F84911E\n' | sfdisk "$output_file"
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
        echo "Linux md RAID10 fixture partition device was not created: $partition" >&2
        exit 4
    fi
done

mkdir -p /dev/md
mdadm --create "$md_device" --run --metadata=1.2 --level=10 --layout=n2 \
    --raid-devices=4 --chunk=64 --name=VDT_MDRAID10 "${partitions[@]}"
mdadm --wait "$md_device"
mkfs.ext4 -F -L VDT_MDRAID10 "$md_device"
mount "$md_device" "$mount_dir"
printf 'Hello from Linux md RAID10\n' > "$mount_dir/hello.txt"
mkdir "$mount_dir/nested"
printf 'inside md RAID10\n' > "$mount_dir/nested/readme.txt"
python3 - "$mount_dir/cross-stripes.bin" <<'PY'
import sys

path = sys.argv[1]
block = bytes(((index * 37 + 11) & 0xff) for index in range(1024 * 1024))
with open(path, "wb") as stream:
    for _ in range(300):
        stream.write(block)
PY
sync
sha256sum "$mount_dir/hello.txt" "$mount_dir/nested/readme.txt" "$mount_dir/cross-stripes.bin"
umount "$mount_dir"
e2fsck -fn "$md_device"
mdadm --detail --export "$md_device"
mdadm --stop "$md_device"
for partition in "${partitions[@]}"; do
    mdadm --examine --export "$partition"
done
for loop_device in "${loops[@]}"; do
    losetup -d "$loop_device"
done
loops=()
sha256sum "${outputs[@]}"
