#!/usr/bin/env bash
# Send N order requests to a running variant, 20 at a time. Each is a trace of its own: a new trace id, so the
# sampler's decision for it is independent of the others.
#
#   scripts/load.sh <port> [count]
set -euo pipefail

port=${1:?usage: scripts/load.sh <port> [count]}
count=${2:-1000}

curl -s -X POST "http://localhost:$port/lab/probe/reset" >/dev/null

started=$(date +%s)
seq 1 "$count" | xargs -P 20 -I{} curl -s -o /dev/null -w '%{http_code}\n' "http://localhost:$port/api/orders/{}" \
  | sort | uniq -c | sed 's/^ */  HTTP /'
echo "  $count orders to :$port in $(( $(date +%s) - started ))s"
echo

curl -s "http://localhost:$port/lab/probe" | python3 -m json.tool
