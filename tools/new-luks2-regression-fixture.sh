#!/usr/bin/env bash
set -euo pipefail

output_file=${1:?usage: new-luks2-regression-fixture.sh OUTPUT_FILE KEY_FILE SIZE_MIB}
key_file=${2:?usage: new-luks2-regression-fixture.sh OUTPUT_FILE KEY_FILE SIZE_MIB}
size_mib=${3:?usage: new-luks2-regression-fixture.sh OUTPUT_FILE KEY_FILE SIZE_MIB}
mount_dir=$(mktemp -d)
loop_device=""
mapper_name="vdt_luks2_$$"
mapper_path="/dev/mapper/$mapper_name"
generation_started=0

cleanup() {
    local status=$?
    trap - EXIT
    set +e
    if mountpoint -q "$mount_dir"; then
        umount "$mount_dir"
    fi
    if [[ -e "$mapper_path" ]]; then
        cryptsetup close "$mapper_name"
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

for command_name in truncate sfdisk losetup cryptsetup mkfs.ext4 mount mountpoint umount sha256sum; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Missing required command: $command_name" >&2
        echo "Install cryptsetup, e2fsprogs, and util-linux in the WSL distribution." >&2
        exit 3
    fi
done

if [[ ! -f "$key_file" ]]; then
    echo "The passphrase file was not found." >&2
    exit 4
fi
if [[ -e "$output_file" ]]; then
    echo "Refusing to overwrite an existing fixture: $output_file" >&2
    exit 2
fi
generation_started=1

truncate -s "${size_mib}M" "$output_file"
printf 'label: gpt\nstart=2048, type=CA7D7CCB-63ED-4C53-861C-1742536059CC\n' | sfdisk "$output_file"

loop_device=$(losetup --find --show --partscan "$output_file")
partition="${loop_device}p1"
for _ in {1..20}; do
    [[ -b "$partition" ]] && break
    sleep 0.1
done
if [[ ! -b "$partition" ]]; then
    echo "The LUKS2 fixture partition device was not created: $partition" >&2
    exit 5
fi

# PBKDF2 is selected explicitly because the first LUKS2 implementation does
# not support Argon2 keyslots. The short target is only for local regression.
cryptsetup luksFormat \
    --type luks2 \
    --batch-mode \
    --cipher aes-xts-plain64 \
    --key-size 512 \
    --hash sha256 \
    --pbkdf pbkdf2 \
    --iter-time 10 \
    --key-file "$key_file" \
    "$partition"
cryptsetup open --type luks2 --key-file "$key_file" "$partition" "$mapper_name"
mkfs.ext4 -F -L VDT_LUKS2 "$mapper_path"
mount "$mapper_path" "$mount_dir"
printf 'LUKS2 PBKDF2 AES-XTS fixture\n' > "$mount_dir/fixture.txt"
sync

stat --format='fixture_size=%s' "$mount_dir/fixture.txt"
sha256sum "$mount_dir/fixture.txt"
cryptsetup luksDump "$partition" | sed -n \
    -e '/^Version:/p' \
    -e '/^Epoch:/p' \
    -e '/^Metadata area:/p' \
    -e '/^Keyslots area:/p' \
    -e '/^[[:space:]]*PBKDF:/p' \
    -e '/^[[:space:]]*Cipher:/p' \
    -e '/^[[:space:]]*Cipher key:/p' \
    -e '/^[[:space:]]*Sector:/p'

umount "$mount_dir"
cryptsetup close "$mapper_name"
losetup -d "$loop_device"
loop_device=""
sha256sum "$output_file"
