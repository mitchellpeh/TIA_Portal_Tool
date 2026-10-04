#!/bin/bash
# Fast check without TIA Portal: convert an SLC export with the console converter (tools/SlcConvert) and print
# the summary and the items that need attention. Takes seconds; run it after every converter change.
#
# Usage: tools/tests/convert-only.sh <export.SLC> [outputFolder] [--1200]
#   outputFolder defaults to tools/tests/out/<export name>.
set -u
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
SLC="$1"; shift
OUTDIR="$REPO/tools/tests/out/$(basename "$SLC" .SLC)"
if [ $# -gt 0 ] && [ "${1#--}" = "$1" ]; then OUTDIR="$1"; shift; fi
dotnet run --project "$REPO/tools/SlcConvert" -c Debug -- "$SLC" "$OUTDIR" "$@"
