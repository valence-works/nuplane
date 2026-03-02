#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

echo "[validate-secrets] scanning repository for credential-like patterns..."

exclude_args=(
  --exclude-dir=.git
  --exclude-dir=bin
  --exclude-dir=obj
  --exclude-dir=.specify
  --exclude=*.snk
)

patterns=(
  'password\s*[:=]\s*[^\s]+'
  'api[_-]?key\s*[:=]\s*[^\s]+'
  'client[_-]?secret\s*[:=]\s*[^\s]+'
  'access[_-]?token\s*[:=]\s*[^\s]+'
  'Authorization:\s*Bearer\s+[A-Za-z0-9._-]+'
  'pk_live_[A-Za-z0-9]+'
  'sk_live_[A-Za-z0-9]+'
)

allowlist=(
  'build/secret-scan-policy.md'
  '.gitignore'
  'README.md'
  'specs/'
)

failures=0

for pattern in "${patterns[@]}"; do
  while IFS= read -r match; do
    [[ -z "$match" ]] && continue

    file_path="${match%%:*}"
    skip=false
    for allowed in "${allowlist[@]}"; do
      if [[ "$file_path" == *"$allowed"* ]]; then
        skip=true
        break
      fi
    done

    if [[ "$skip" == false ]]; then
      echo "[validate-secrets] potential secret: $match"
      failures=$((failures + 1))
    fi
  done < <(grep -RInE "${pattern}" . "${exclude_args[@]}" || true)
done

if [[ $failures -gt 0 ]]; then
  echo "[validate-secrets] FAILED with $failures potential credential findings."
  exit 1
fi

echo "[validate-secrets] OK - no committed source credentials detected."
