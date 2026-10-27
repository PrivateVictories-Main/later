#!/usr/bin/env bash
# Remote CI helper for the Windows port (there is no local Windows or .NET).
#   scripts/win-ci.sh watch                   wait for every workflow run of HEAD, print each job's conclusion
#   scripts/win-ci.sh log <job> [regex]       print the matching lines of a job's log (job name prefix, e.g. "build")
#   scripts/win-ci.sh fetch <artifact> <dir>  download an artifact into a new, empty directory
#   scripts/win-ci.sh purge                   delete every Actions artifact of HEAD's runs
# Run `watch` in the background: it blocks until the runs finish.
set -euo pipefail
repo=PrivateVictories-Main/later
sha=$(git rev-parse HEAD)
run_ids() { gh run list --repo "$repo" --commit "$sha" --limit 20 --json databaseId --jq '.[].databaseId'; }

case "${1:-}" in
  watch)
    ids=""
    for _ in $(seq 1 60); do ids=$(run_ids); [ -n "$ids" ] && break; sleep 5; done
    [ -n "$ids" ] || { echo "no workflow run for $sha after 5 minutes" >&2; exit 1; }
    sleep 15   # the other workflows of the same push register a moment later
    ids=$(run_ids)
    rc=0
    for id in $ids; do gh run watch "$id" --repo "$repo" --exit-status > /dev/null 2>&1 || rc=1; done
    for id in $ids; do
      gh run view "$id" --repo "$repo" --json name,conclusion,jobs \
        --jq '"\(.name): \(.conclusion)", (.jobs[] | "  \(.name): \(.conclusion)")'
    done
    exit $rc ;;
  log)
    job=${2:?job name}; pattern=${3:-.}
    for id in $(run_ids); do
      for j in $(gh run view "$id" --repo "$repo" --json jobs --jq ".jobs[] | select(.name | startswith(\"$job\")) | .databaseId"); do
        echo "--- job $j"
        gh run view --repo "$repo" --job "$j" --log | grep -E "$pattern" || true
      done
    done ;;
  fetch)
    name=${2:?artifact}; dir=${3:?directory}
    [ -z "$(ls -A "$dir" 2>/dev/null || true)" ] || { echo "$dir is not empty" >&2; exit 1; }
    mkdir -p "$dir"
    for id in $(run_ids); do
      gh run download "$id" --repo "$repo" -n "$name" -D "$dir" 2> /dev/null && exit 0
    done
    echo "artifact $name not found" >&2; exit 1 ;;
  purge)
    for id in $(run_ids); do
      for a in $(gh api "repos/$repo/actions/runs/$id/artifacts" --jq '.artifacts[].id'); do
        gh api -X DELETE "repos/$repo/actions/artifacts/$a" > /dev/null && echo "deleted artifact $a"
      done
    done ;;
  *) sed -n '2,7p' "$0"; exit 64 ;;
esac
