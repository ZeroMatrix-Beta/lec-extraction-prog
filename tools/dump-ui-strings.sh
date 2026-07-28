#!/usr/bin/env bash
# Extracts every user-facing console string in the project into a sorted,
# deduplicated, location-independent inventory.
#
# This is the characterization harness for the refactor: the project has no
# end-to-end tests and the pipeline can only be exercised against a paid API,
# so the console output IS the specification. Refactoring phases must leave
# this inventory byte-identical.
#
# Usage:
#   tools/dump-ui-strings.sh > docs/ui-strings.baseline.txt   # capture
#   tools/dump-ui-strings.sh | diff docs/ui-strings.baseline.txt -   # verify
#
# Sorting and deduplicating makes the output independent of which file a
# string lives in and where it sits in that file, so moving code between
# files during the refactor produces no diff -- only *changing a string* does.

set -euo pipefail
cd "$(dirname "$0")/.."

find . -name '*.cs' \
    -not -path './obj/*' \
    -not -path './bin/*' \
    -not -path './build_test_bin/*' \
    -not -path './tests/*' \
    -print0 \
  | xargs -0 grep -hoE '(Console\.(Error\.)?(Write|WriteLine)|Ui\.(Info|Warn|Error|Success|Step|Detail|Raw|RawLine))\(.*' \
  | sed -e 's/[[:space:]]\+$//' \
  | sort -u
