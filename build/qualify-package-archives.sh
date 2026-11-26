#!/usr/bin/env bash
# Qualifies archive-backed package content: the reader and installer unit tests and the AddNuplane acceptance
# tests, each required by name and passed with no skips. NUPLANE_ARCHIVE_TEST_ROOT and NUPLANE_EXPECT_FILESYSTEM
# (see PackageArchiveReaderAcceptanceTests) select and assert the filesystem under test.
set -euo pipefail

results_directory=${1:?Usage: qualify-package-archives.sh RESULTS_DIRECTORY}
if [[ "${RUNNER_OS:-}" == Windows ]]; then python=python; else python=python3; fi
# Unit tests use the temporary directory, so put it on the filesystem under test too.
if [[ -n "${NUPLANE_ARCHIVE_TEST_ROOT:-}" ]]; then
  export TMPDIR="$NUPLANE_ARCHIVE_TEST_ROOT/tmp"
  mkdir -p "$TMPDIR"
fi

dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj \
  --configuration Release --framework net10.0 \
  --filter "FullyQualifiedName~Nuplane.Runtime.Tests.PackageArchive|FullyQualifiedName~Nuplane.Runtime.Tests.Store.StoreStateRecordTests|FullyQualifiedName~Nuplane.Runtime.Tests.Store.StoreRegistryTests|FullyQualifiedName~Nuplane.Runtime.Tests.Operational.ActivePackageCatalogMapperTests|FullyQualifiedName~Nuplane.Runtime.Tests.Feeds.PackageInstallStoreTests.InstallAsync_Archive" \
  --blame-hang-timeout 3m \
  --logger "trx;LogFileName=package-archive-reader.trx" \
  --results-directory "$results_directory/reader"
reader_manifest=build/test-cases/package-archive-reader.json
if [[ "${RUNNER_OS:-}" == Windows ]]; then reader_manifest=build/test-cases/package-archive-reader-windows.json; fi
"$python" build/verify-required-trx.py "$results_directory/reader/package-archive-reader.trx" "$reader_manifest"

dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj \
  --configuration Release --framework net10.0 \
  --filter "FullyQualifiedName~Nuplane.Integration.Tests.Content.PackageArchiveReaderAcceptanceTests" \
  --blame-hang-timeout 3m \
  --logger "trx;LogFileName=package-archive-acceptance.trx" \
  --results-directory "$results_directory/acceptance"
"$python" build/verify-required-trx.py "$results_directory/acceptance/package-archive-acceptance.trx" \
  build/test-cases/package-archive-acceptance.json
