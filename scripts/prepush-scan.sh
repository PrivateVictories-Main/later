#!/usr/bin/env bash
# Fails when the branch holds personal data, tool or agent names, or a non-noreply author. Run before every push.
set -euo pipefail
base=${1:-main}
personal='/Users/|C:\\Users\\|ryan|\.superpowers|co-authored|[A-Za-z0-9._-]+@[A-Za-z0-9.-]+\.[a-z]+'
tools='codex|claude|anthropic|agentdesk|opus|sonnet|gpt-[0-9]|t3 ?code'
status=0
self=':(exclude)scripts/prepush-scan.sh'   # holds the patterns themselves
added() { git diff "$base"...HEAD -U0 -- "$@" | grep '^+' | grep -v '^+++' || true; }
if added . ':(exclude)LICENSE' ':(exclude)*.png' ':(exclude)*.ico' ':(exclude)shared/vectors' "$self" | grep -inE "$personal" | grep -v 'users.noreply.github.com' ; then
  echo "prepush-scan: personal data in added lines" >&2; status=1; fi
if added . ':(exclude)shared/vectors' ':(exclude)Tests' "$self" | grep -inE "$tools"; then
  echo "prepush-scan: tool or agent names in added lines" >&2; status=1; fi
if git log "$base"..HEAD --format=%B | grep -inE "$personal|$tools"; then
  echo "prepush-scan: commit messages need cleaning" >&2; status=1; fi
if git log "$base"..HEAD --format='%ae%n%ce' | sort -u | grep -v 'users.noreply.github.com'; then
  echo "prepush-scan: author or committer email is not a noreply address (git config user.email <id>+<login>@users.noreply.github.com)" >&2; status=1; fi
[ $status -eq 0 ] && echo "prepush-scan: clean"
exit $status
