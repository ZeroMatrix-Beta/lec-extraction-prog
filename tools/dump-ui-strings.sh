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

# Every clause below ends in "|| true" because grep exits 1 when it matches nothing, and under
# "set -e" that aborts the script *silently in the middle of the group* -- every later clause
# simply never runs and its strings vanish from the inventory as if they had been deleted from the
# source. That is not hypothetical: it happened the moment the bare-Write clause below stopped
# matching (2026-07-29), which took the ChatCommand.Error clause down with it. A clause matching
# nothing is a legitimate state here; a clause being skipped is not.
{
  # Qualified calls: Console.WriteLine(...) and the Ui/prompt helpers.
  sources | xargs -0 grep -hoE '(Console\.(Error\.)?(Write|WriteLine)|Ui\.(Info|Warn|Error|Success|Step|Detail|Raw|RawLine|Header|Confirm|ConfirmOrBack|Select|Ask|Table)|SetupQuestionPrompt\.Ask)\(.*' || true

  # Bare Write/WriteLine, reached via "using static System.Console".
  #
  # Both chat sessions and AttachmentUploader.cs did this, so until 2026-07-29 every user-facing
  # string in them -- ~1400 lines -- was invisible to this inventory. They have since moved onto
  # the Ui layer and no file in src/ carries "using static System.Console" any more, so this clause
  # now matches nothing. It is kept deliberately: if the shorthand comes back, the strings it hides
  # come back into the inventory with it rather than silently disappearing.
  #
  # Anchored to start-of-line whitespace so "sw.WriteLine(...)" and other non-console writers
  # do not match; the leading indent is stripped so the entry matches the qualified form.
  { sources | xargs -0 grep -hoE '^[[:space:]]+(Write|WriteLine)\(.*' || true; } | sed -e 's/^[[:space:]]*//'

  # ChatCommand.Error -- German messages the parser produces and the sessions print via
  # WriteLine($"[FEHLER] {command.Error}").
  #
  # Separating parsing from printing (8.5b increment 2) moved these out of the literal-argument
  # shape the two greps above match, so the inventory silently lost them the moment the second
  # session stopped carrying its own copies. They are user-facing text and belong here.
  { sources | xargs -0 grep -hoE 'Error = \$?".*' || true; } | sed -e 's/^[[:space:]]*//'
} \
  | sed -e 's/[[:space:]]\+$//' \
  | sort -u
