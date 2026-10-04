#!/bin/bash
# End-to-end SLC test against real TIA Portal: build the app, convert an SLC export, create a fresh TIA project,
# import, compile and verify every converted rung against the SLC source (SLC_Verification.xlsx).
#
# Usage (Git Bash, from anywhere):
#   tools/tests/tia-test.sh <export.SLC> <projectName> [--release] [--1200] [--no-verify]
#
#   <projectName>  test project created (replaced if it exists) in $SLC_TEST_PROJECTS
#                  (default Documents\Automation\SlcTests). Output folder: Desktop\SLCConvert\<projectName>_test.
#   --release      build Release instead of Debug. Release also overwrites the portable TiaPortalTool.exe in the
#                  repo root, so only use it when a new exe is wanted.
#
# Runs through tools/runner/OpennessRunner.exe (build it once, see tools/tests/README.md). TIA takes 5-20 minutes;
# the full log goes to tools/tests/out/<projectName>.log and the problems are printed at the end.
set -u
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
SLC="$1"; NAME="$2"; shift 2
CONFIG=Debug; VERIFY=--verify; EXTRA=()
for a in "$@"; do
  case "$a" in
    --release) CONFIG=Release ;;
    --no-verify) VERIFY= ;;
    *) EXTRA+=("$a") ;;
  esac
done
PROJECTS="${SLC_TEST_PROJECTS:-$USERPROFILE/Documents/Automation/SlcTests}"
OUT="$REPO/tools/tests/out"; mkdir -p "$OUT"; LOG="$OUT/$NAME.log"

# The build fails while the app has the exe open.
powershell -NoProfile -Command "Get-Process TiaPortalTool -ErrorAction SilentlyContinue | Stop-Process -Force"
BUILD=$(cd "$REPO/TiaPortalTool" && dotnet build -c "$CONFIG" 2>&1 | grep -E " error |Error\(s\)" | head -5)
echo "$BUILD"; echo "$BUILD" | grep -q " error " && exit 1

timeout 1500 "$REPO/tools/runner/OpennessRunner.exe" "$REPO/TiaPortalTool/bin/$CONFIG/net48/TiaPortalTool.exe" \
  slc-import --slc "$SLC" --out "$USERPROFILE/Desktop/SLCConvert/${NAME}_test" --new "$PROJECTS" "$NAME" --replace \
  $VERIFY ${EXTRA[@]+"${EXTRA[@]}"} > "$LOG" 2>&1
echo "exit $?" >> "$LOG"

# Problems only: drop the per-block "Imported" lines, keep failures, compile results, ladder and verification summaries.
grep -v "Imported \|created block group\|^\s*$" "$LOG" | grep -n "" \
  | grep -A3 -i "fail\|error\|Ladder:\|RESULT\|exit\|compile" | cut -c1-700 | head -150
echo "Full log: $LOG"
