#!/usr/bin/env bash
set -euo pipefail

first_output=${1:?usage: new-lvm-multi-pv-regression-fixture.sh FIRST_OUTPUT SECOND_OUTPUT SIZE_MIB}
second_output=${2:?usage: new-lvm-multi-pv-regression-fixture.sh FIRST_OUTPUT SECOND_OUTPUT SIZE_MIB}
size_mib=${3:?usage: new-lvm-multi-pv-regression-fixture.sh FIRST_OUTPUT SECOND_OUTPUT SIZE_MIB}
mount_dir=$(mktemp -d)
first_loop=""
second_loop=""
vg_name="VDT_LVM_MULTIPV"
lv_name="data"
generation_started=0
vg_created=0

cleanup() {
    local status=$?
    trap - EXIT
    set +e
    if mountpoint -q "$mount_dir"; then
        umount "$mount_dir"
    fi
    if [[ $vg_created -eq 1 ]]; then
        vgchange -an "$vg_name"
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

for command_name in truncate sfdisk losetup pvcreate vgcreate vgchange lvcreate lvextend lvs mkfs.ext4 e2fsck mount mountpoint umount sha256sum python3; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Missing required command: $command_name" >&2
        echo "Install lvm2, e2fsprogs, python3, and util-linux in the WSL distribution." >&2
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
if (( size_mib < 256 )); then
    echo "Fixture size must be at least 256 MiB per disk." >&2
    exit 2
fi
generation_started=1

for output_file in "$first_output" "$second_output"; do
    truncate -s "${size_mib}M" "$output_file"
    printf 'label: gpt\nstart=2048, type=E6D6D379-F507-44C2-A23C-238F2A3DF928\n' | sfdisk "$output_file"
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
    echo "LVM2 fixture partition devices were not created." >&2
    exit 4
fi

pvcreate --yes --force "$first_partition" "$second_partition"
vgcreate "$vg_name" "$first_partition" "$second_partition"
vg_created=1
lvcreate --yes --name "$lv_name" --size 128M "$vg_name" "$first_partition"
lvextend --yes --size +128M "$vg_name/$lv_name" "$second_partition"
lv_path="/dev/$vg_name/$lv_name"

segment_report=$(lvs --noheadings --separator '|' --segments -o segtype,devices "$lv_path")
printf '%s\n' "$segment_report"
if ! grep -Fq "$first_partition" <<<"$segment_report" || ! grep -Fq "$second_partition" <<<"$segment_report"; then
    echo "Generated LV does not contain segments on both PVs." >&2
    exit 5
fi

mkfs.ext4 -F -L VDT_LVM_MULTIPV "$lv_path"
mount "$lv_path" "$mount_dir"
printf 'Hello from LVM2 multi-PV linear LV\n' > "$mount_dir/hello.txt"
mkdir "$mount_dir/nested"
printf 'This filesystem spans two physical volumes.\n' > "$mount_dir/nested/readme.txt"
python3 - "$mount_dir/cross-pv.bin" <<'PY'
import sys

path = sys.argv[1]
block = bytes(((index * 17 + 31) & 0xff) for index in range(1024 * 1024))
with open(path, "wb") as stream:
    for _ in range(160):
        stream.write(block)
PY
sync
sha256sum "$mount_dir/hello.txt" "$mount_dir/nested/readme.txt" "$mount_dir/cross-pv.bin"
umount "$mount_dir"
e2fsck -fn "$lv_path"
pvs --units b -o pv_name,pv_uuid,vg_name,pv_size,pv_free
vgs --units b -o vg_name,vg_uuid,vg_size,vg_free,pv_count,lv_count
lvs --units b --segments -o lv_name,lv_uuid,lv_size,segtype,seg_size,devices "$lv_path"
vgchange -an "$vg_name"
vg_created=0
losetup -d "$first_loop"
first_loop=""
losetup -d "$second_loop"
second_loop=""
sha256sum "$first_output" "$second_output"
