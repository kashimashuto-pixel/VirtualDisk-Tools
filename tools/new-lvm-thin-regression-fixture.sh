#!/usr/bin/env bash
set -euo pipefail

size_mib=${1:?usage: new-lvm-thin-regression-fixture.sh SIZE_MIB OUTPUT}
output=${2:?usage: new-lvm-thin-regression-fixture.sh SIZE_MIB OUTPUT}
mount_dir=$(mktemp -d)
loop_device=""
partition=""
vg_name="vdt_thin_$$"
vg_created=0
generation_started=0

cleanup() {
    local status=$?
    trap - EXIT
    set +e
    if mountpoint -q "$mount_dir"; then
        umount "$mount_dir"
    fi
    if [[ $vg_created -eq 1 ]] && vgs "$vg_name" >/dev/null 2>&1; then
        vgchange -an "$vg_name"
        vgremove -ff -y "$vg_name"
    fi
    if [[ -n "$partition" && -b "$partition" ]]; then
        pvremove -ff -y "$partition"
    fi
    if [[ -n "$loop_device" ]]; then
        losetup -d "$loop_device"
    fi
    rmdir "$mount_dir"
    if [[ $status -ne 0 && $generation_started -eq 1 ]]; then
        rm -f -- "$output"
    fi
    exit "$status"
}
trap cleanup EXIT

for command_name in truncate sfdisk losetup pvcreate vgcreate lvcreate lvchange vgs vgchange vgremove pvremove mkfs.ext4 e2fsck mount mountpoint umount sha256sum python3 thin_check thin_dump; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Missing required command: $command_name" >&2
        echo "Install lvm2, thin-provisioning-tools, e2fsprogs, python3, and util-linux in the WSL distribution." >&2
        exit 3
    fi
done
if [[ -e "$output" ]]; then
    echo "Refusing to overwrite an existing fixture: $output" >&2
    exit 2
fi
if (( size_mib < 384 )); then
    echo "LVM2 thin fixture size must be at least 384 MiB." >&2
    exit 2
fi
generation_started=1

truncate -s "${size_mib}M" "$output"
printf 'label: gpt\nstart=2048, type=E6D6D379-F507-44C2-A23C-238F2A3DF928\n' | sfdisk "$output"
loop_device=$(losetup --find --show --partscan "$output")
partition="${loop_device}p1"
for _ in {1..20}; do
    [[ -b "$partition" ]] && break
    sleep 0.1
done
if [[ ! -b "$partition" ]]; then
    echo "LVM2 thin fixture partition device was not created: $partition" >&2
    exit 4
fi

pvcreate -ff -y --metadatasize 1M "$partition"
vgcreate --physicalextentsize 4M "$vg_name" "$partition"
vg_created=1
lvcreate --yes --type thin-pool --size 256M --poolmetadatasize 8M --chunksize 64K --name pool "$vg_name"
lvcreate --yes --virtualsize 384M --thinpool pool --name thinvol "$vg_name"
mkfs.ext4 -F -L VDT_LVM_THIN "/dev/$vg_name/thinvol"
mount "/dev/$vg_name/thinvol" "$mount_dir"
printf 'Hello from an LVM2 thin volume\n' > "$mount_dir/hello.txt"
mkdir "$mount_dir/nested"
printf 'inside LVM2 thin provisioning\n' > "$mount_dir/nested/readme.txt"
python3 - "$mount_dir/cross-chunks.bin" <<'PY'
import sys

path = sys.argv[1]
block = bytes(((index * 47 + 13) & 0xff) for index in range(1024 * 1024))
with open(path, "wb") as stream:
    for _ in range(160):
        stream.write(block)
PY
sync
sha256sum "$mount_dir/hello.txt" "$mount_dir/nested/readme.txt" "$mount_dir/cross-chunks.bin"
umount "$mount_dir"
e2fsck -fn "/dev/$vg_name/thinvol"
lvs --all --units s --nosuffix -o lv_name,lv_uuid,lv_attr,segtype,seg_start_pe,seg_size_pe,devices,metadata_lv,pool_lv,data_lv,transaction_id,thin_id,chunksize "$vg_name"
vgcfgbackup --file /dev/stdout "$vg_name"
lvchange -an "$vg_name/thinvol"
lvchange -an "$vg_name/pool"
lvchange -ay -K --yes "$vg_name/pool_tmeta"
thin_check "/dev/$vg_name/pool_tmeta"
thin_dump --skip-mappings "/dev/$vg_name/pool_tmeta"
lvchange -an "$vg_name/pool_tmeta"
vgchange -an "$vg_name"
vg_created=0
losetup -d "$loop_device"
loop_device=""
sha256sum "$output"
