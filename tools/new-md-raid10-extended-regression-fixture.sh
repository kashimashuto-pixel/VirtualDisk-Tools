#!/usr/bin/env bash
set -euo pipefail

layout_name=${1:?usage: new-md-raid10-extended-regression-fixture.sh far|offset SIZE_MIB OUTPUT1 OUTPUT2}
size_mib=${2:?usage: new-md-raid10-extended-regression-fixture.sh far|offset SIZE_MIB OUTPUT1 OUTPUT2}
shift 2
outputs=("$@")
mount_dir=$(mktemp -d)
loops=()
partitions=()
md_device="/dev/md/vdt-mdraid10-${layout_name}-$$"
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

if [[ "$layout_name" == "far" ]]; then
    mdadm_layout="f2"
    array_label="VDT_MDRAID10_FAR"
elif [[ "$layout_name" == "offset" ]]; then
    mdadm_layout="o2"
    array_label="VDT_MDRAID10_OFFSET"
else
    echo "Layout must be far or offset." >&2
    exit 2
fi
if [[ ${#outputs[@]} -ne 2 ]]; then
    echo "RAID10 extended-layout fixture requires exactly two output files." >&2
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
mdadm --create "$md_device" --run --metadata=1.2 --level=10 --layout="$mdadm_layout" \
    --raid-devices=2 --chunk=64 --name="$array_label" "${partitions[@]}"
mdadm --wait "$md_device"
mkfs.ext4 -F -L "$array_label" "$md_device"
mount "$md_device" "$mount_dir"
printf 'Hello from Linux md RAID10 %s layout\n' "$layout_name" > "$mount_dir/hello.txt"
mkdir "$mount_dir/nested"
printf 'inside md RAID10 %s layout\n' "$layout_name" > "$mount_dir/nested/readme.txt"
python3 - "$mount_dir/cross-stripes.bin" <<'PY'
import sys

path = sys.argv[1]
block = bytes(((index * 43 + 5) & 0xff) for index in range(1024 * 1024))
with open(path, "wb") as stream:
    for _ in range(160):
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
