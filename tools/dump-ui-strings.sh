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

sources() {
  find . -name '*.cs' \
      -not -path './obj/*' \
      -not -path './bin/*' \
      -not -path './build_test_bin/*' \
      -not -path './tests/*' \
      -print0
}

{
  # Qualified calls: Console.WriteLine(...) and the Ui/prompt helpers.
  sources | xargs -0 grep -hoE '(Console\.(Error\.)?(Write|WriteLine)|Ui\.(Info|Warn|Error|Success|Step|Detail|Raw|RawLine|Header|Confirm|ConfirmOrBack|Select|Ask|Table)|SetupQuestionPrompt\.Ask)\(.*'

  # Bare Write/WriteLine, reached via "using static System.Console".
  #
  # Both chat sessions do this, so until 2026-07-29 every user-facing string in
  # DirectAiChatSessionAiStudio.cs and DirectAiChatSessionVertex.cs -- ~1400 lines -- was
  # invisible to this inventory. That is precisely the code Phase 8.5b rewrites, so the
  # characterization harness the plan relies on did not in fact cover it.
  #
  # Anchored to start-of-line whitespace so "sw.WriteLine(...)" and other non-console writers
  # do not match; the leading indent is stripped so the entry matches the qualified form.
  sources | xargs -0 grep -hoE '^[[:space:]]+(Write|WriteLine)\(.*' | sed -e 's/^[[:space:]]*//'
} \
  | sed -e 's/[[:space:]]\+$//' \
  | sort -u
