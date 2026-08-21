#!/usr/bin/env bash
set -euo pipefail

source_file=${1:?usage: new-ewf-regression-fixture.sh SOURCE_RAW OUTPUT_E01 SEGMENT_SIZE_MIB}
output_file=${2:?usage: new-ewf-regression-fixture.sh SOURCE_RAW OUTPUT_E01 SEGMENT_SIZE_MIB}
segment_size_mib=${3:?usage: new-ewf-regression-fixture.sh SOURCE_RAW OUTPUT_E01 SEGMENT_SIZE_MIB}
output_base=${output_file%.*}
output_directory=$(dirname -- "$output_file")
output_name=$(basename -- "$output_base")
generation_started=0

cleanup() {
    local status=$?
    trap - EXIT
    if [[ $status -ne 0 && $generation_started -eq 1 ]]; then
        find "$output_directory" -maxdepth 1 -type f -name "${output_name}.E[0-9][0-9]" -delete
    fi
    exit "$status"
}
trap cleanup EXIT

for command_name in ewfacquire ewfverify sha256sum find grep sort xargs; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        echo "Missing required command: $command_name" >&2
        echo "Install ewf-tools in the WSL distribution." >&2
        exit 3
    fi
done
if [[ ! -f "$source_file" ]]; then
    echo "Source RAW image was not found: $source_file" >&2
    exit 4
fi
if find "$output_directory" -maxdepth 1 -type f -name "${output_name}.E[0-9][0-9]" -print -quit | grep -q .; then
    echo "Refusing to overwrite existing EWF segments for: $output_base" >&2
    exit 2
fi

generation_started=1
ewfacquire \
    -u \
    -q \
    -f encase6 \
    -c fast \
    -S "${segment_size_mib}MiB" \
    -t "$output_base" \
    "$source_file"
ewfverify -q "$output_file"

echo 'Source RAW SHA-256:'
sha256sum "$source_file"
echo 'EWF segment SHA-256:'
find "$output_directory" -maxdepth 1 -type f -name "${output_name}.E[0-9][0-9]" -print0 \
    | sort -z \
    | xargs -0 sha256sum
