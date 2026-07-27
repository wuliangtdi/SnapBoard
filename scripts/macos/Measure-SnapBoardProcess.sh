#!/bin/zsh

set -euo pipefail

script_dir=${0:A:h}
executable_path=${1:-}
runs=${2:-3}
sample_seconds=${3:-10}
background_seconds=${4:-3}

if [[ -z "$executable_path" || ! -x "$executable_path" ]]; then
    print -u2 "usage: $0 <executable-path> [runs=3] [sample-seconds=10] [background-seconds=3]"
    exit 2
fi

if ! [[ "$runs" =~ '^[1-9][0-9]*$' && "$sample_seconds" =~ '^[1-9][0-9]*$' &&
    "$background_seconds" =~ '^[1-9][0-9]*$' ]]; then
    print -u2 "runs, sample-seconds, and background-seconds must be positive integers"
    exit 2
fi

temporary_dir=$(mktemp -d "${TMPDIR:-/tmp}/snapboard-measure.XXXXXX")
probe_path="$temporary_dir/snapboard_process_probe"
process_id=""

function cleanup {
    if [[ -n "$process_id" ]] && kill -0 "$process_id" 2>/dev/null; then
        "$executable_path" --exit >/dev/null 2>&1 || kill -TERM "$process_id" 2>/dev/null || true
        wait "$process_id" 2>/dev/null || true
    fi

    rm -rf -- "$temporary_dir"
}

trap cleanup EXIT

xcrun clang \
    -O2 \
    -Wall \
    -Wextra \
    -Werror \
    -framework CoreFoundation \
    -framework CoreGraphics \
    "$script_dir/snapboard_process_probe.c" \
    -o "$probe_path"

print "run,startup_ms,max_window_phys_mib,max_window_rss_mib,background_phys_mib,background_rss_mib,phys_return_mib,lifetime_peak_phys_mib,max_threads,max_file_descriptors,avg_cpu_percent,energy_mj,interrupt_wakeups"

for run in $(seq 1 "$runs"); do
    process_log="$temporary_dir/run-$run.log"
    start_ns=$($probe_path now 0)
    "$executable_path" >"$process_log" 2>&1 &
    process_id=$!

    window_deadline=$((SECONDS + 15))
    while ! "$probe_path" window "$process_id"; do
        if ! kill -0 "$process_id" 2>/dev/null; then
            print -u2 "SnapBoard exited before opening a window (run $run)"
            sed -n '1,80p' "$process_log" >&2
            exit 3
        fi

        if (( SECONDS >= window_deadline )); then
            kill -TERM "$process_id" 2>/dev/null || true
            wait "$process_id" 2>/dev/null || true
            print -u2 "Timed out waiting for the main window (run $run)"
            exit 4
        fi

        sleep 0.01
    done

    window_ns=$($probe_path now 0)
    startup_ms=$(awk -v start="$start_ns" -v finish="$window_ns" \
        'BEGIN { printf "%.2f", (finish - start) / 1000000 }')

    first_sample=""
    last_sample=""
    max_phys=0
    max_rss=0
    max_peak=0
    max_threads=0
    max_file_descriptors=0

    for second in $(seq 0 "$sample_seconds"); do
        sample=$($probe_path usage "$process_id")
        first_sample=${first_sample:-$sample}
        last_sample=$sample

        IFS=',' read -r rss phys peak user system energy wakeups threads file_descriptors <<< "$sample"
        (( rss > max_rss )) && max_rss=$rss
        (( phys > max_phys )) && max_phys=$phys
        (( peak > max_peak )) && max_peak=$peak
        (( threads > max_threads )) && max_threads=$threads
        (( file_descriptors > max_file_descriptors )) && max_file_descriptors=$file_descriptors

        (( second < sample_seconds )) && sleep 1
    done

    "$executable_path" --close-windows
    close_deadline=$((SECONDS + 10))
    while "$probe_path" window "$process_id"; do
        if (( SECONDS >= close_deadline )); then
            print -u2 "Timed out waiting for windows to close (run $run)"
            exit 5
        fi

        sleep 0.05
    done

    background_sample=""
    for second in $(seq 1 "$background_seconds"); do
        sleep 1
        background_sample=$($probe_path usage "$process_id")
        IFS=',' read -r _ _ peak _ _ _ _ threads file_descriptors <<< "$background_sample"
        (( peak > max_peak )) && max_peak=$peak
        (( threads > max_threads )) && max_threads=$threads
        (( file_descriptors > max_file_descriptors )) && max_file_descriptors=$file_descriptors
    done

    IFS=',' read -r _ _ _ first_user first_system first_energy first_wakeups _ _ <<< "$first_sample"
    IFS=',' read -r background_rss background_phys _ last_user last_system last_energy last_wakeups _ _ <<< "$background_sample"
    cpu_delta=$((last_user + last_system - first_user - first_system))
    energy_delta=$((last_energy - first_energy))
    wakeup_delta=$((last_wakeups - first_wakeups))

    max_phys_mib=$(awk -v bytes="$max_phys" 'BEGIN { printf "%.2f", bytes / 1048576 }')
    max_rss_mib=$(awk -v bytes="$max_rss" 'BEGIN { printf "%.2f", bytes / 1048576 }')
    max_peak_mib=$(awk -v bytes="$max_peak" 'BEGIN { printf "%.2f", bytes / 1048576 }')
    background_phys_mib=$(awk -v bytes="$background_phys" 'BEGIN { printf "%.2f", bytes / 1048576 }')
    background_rss_mib=$(awk -v bytes="$background_rss" 'BEGIN { printf "%.2f", bytes / 1048576 }')
    phys_return_mib=$(awk -v peak="$max_phys" -v background="$background_phys" \
        'BEGIN { printf "%.2f", (peak - background) / 1048576 }')
    avg_cpu=$(awk -v cpu="$cpu_delta" -v seconds="$sample_seconds" \
        -v background_seconds="$background_seconds" \
        'BEGIN { printf "%.3f", cpu / ((seconds + background_seconds) * 1000000000) * 100 }')
    energy_mj=$(awk -v energy="$energy_delta" 'BEGIN { printf "%.3f", energy / 1000000 }')

    print "$run,$startup_ms,$max_phys_mib,$max_rss_mib,$background_phys_mib,$background_rss_mib,$phys_return_mib,$max_peak_mib,$max_threads,$max_file_descriptors,$avg_cpu,$energy_mj,$wakeup_delta"

    "$executable_path" --exit
    wait "$process_id"
    process_id=""
    sleep 1
done
