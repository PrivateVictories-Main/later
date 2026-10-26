#!/bin/zsh
# Reports Later's resource use. Usage:
#   scripts/measure.sh memory           memory footprint now
#   scripts/measure.sh quiet [minutes]  per-minute CPU %, idle wake-ups and ring count (default 10)
#   scripts/measure.sh rings [minutes]  per-ring read and judge times (default 10)
set -euo pipefail
# The installed copy only: development builds (com.ryan.later.dev) log to the same subsystem.
pid=$(pgrep -f '/Applications/Later.app/Contents/MacOS/Later' | head -1) || { echo "/Applications/Later.app isn't running" >&2; exit 1; }
mode=${1:-quiet}
minutes=${2:-10}
tmp=$(mktemp -t later-measure)
trap 'rm -f "$tmp"' EXIT

case $mode in
  memory)
    footprint "$pid" | grep -iE 'footprint' | head -2 ;;
  quiet)
    /usr/bin/log stream --level info --style compact --predicate "subsystem == \"com.ryan.later\" AND category == \"perf\" AND processID == $pid" > "$tmp" &
    logger=$!
    echo "minute  cpu%  idle-wakeups  rings"
    # One top call per minute: a long-running top buffers its output through the pipe, so rows (and their
    # ring counts) would all arrive at the end. The second sample of each call covers the minute.
    for _ in $(seq "$minutes"); do
      : > "$tmp"
      read -r cpu wakeups < <(top -l 2 -s 60 -pid "$pid" -stats pid,cpu,idlew | awk -v p="$pid" '$1 == p { v = $2 " " $3 } END { print v }')
      rings=$(grep -c ' ring ' "$tmp" || true)
      echo "$(date +%H:%M)  $cpu  $wakeups  $rings"
    done
    kill $logger
    footprint "$pid" | grep -iE 'footprint' | head -2 ;;
  rings)
    /usr/bin/log stream --level info --style compact --predicate "subsystem == \"com.ryan.later\" AND category == \"perf\" AND processID == $pid" > "$tmp" &
    logger=$!
    sleep $((minutes * 60))
    kill $logger
    awk '/ ring / {
      for (i = 1; i <= NF; i++) {
        if ($i ~ /^read=/)  { v = $i; gsub(/read=|ms/, "", v); r += v; if (v > rm) rm = v }
        if ($i ~ /^judge=/) { v = $i; gsub(/judge=|ms/, "", v); j += v; if (v > jm) jm = v }
      }
      n++
    } END { if (n) printf "%d rings: read avg %.1f ms (max %.1f), judge avg %.2f ms (max %.2f)\n", n, r/n, rm, j/n, jm; else print "no rings" }' "$tmp" ;;
esac
