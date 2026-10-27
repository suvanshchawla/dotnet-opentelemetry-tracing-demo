#!/usr/bin/env bash
# Usage: scripts/bench.sh [path] [runs]   e.g. scripts/bench.sh /listings/search/slow 30
# Fires <runs> sequential requests (after 3 warm-ups) and prints min / median / p95 / max in ms,
# measured client-side by curl (includes network + JSON serialisation, so a bit above server-side time).
path="${1:-/listings/search/slow}"; runs="${2:-30}"; base="${BASE_URL:-http://localhost:8080}"
for _ in 1 2 3; do curl -s -o /dev/null "$base$path"; done
for _ in $(seq "$runs"); do curl -s -o /dev/null -w '%{time_total}\n' "$base$path"; done |
  sort -n | awk -v p="$path" '{a[NR]=$1*1000} END{printf "%s  n=%d  min=%.1fms  median=%.1fms  p95=%.1fms  max=%.1fms\n", p, NR, a[1], a[int((NR+1)/2)], a[int(NR*0.95)], a[NR]}'
