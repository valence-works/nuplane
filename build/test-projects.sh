#!/usr/bin/env bash
set -euo pipefail

results_directory=${1:?Usage: test-projects.sh RESULTS_DIRECTORY}
for project in test/*.Tests/*.csproj; do
  name=$(basename "${project%.csproj}")
  dotnet test "$project" --configuration Release --no-restore --no-build \
    --blame-crash --logger "trx;LogFileName=$name.trx" \
    --results-directory "$results_directory/$name"
done
