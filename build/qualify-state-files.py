"""Run the exact native state I/O and retained payload-resolution cases for one provider."""
import argparse
import subprocess
import sys
from pathlib import Path


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("provider", choices=("Unix", "Windows"))
parser.add_argument("results_directory", type=Path)
args = parser.parse_args()
trx = args.results_directory / "state-files.trx"
selected_tests = (
    f"FullyQualifiedName~{args.provider}PhysicalStoreStateFileTests|"
    "FullyQualifiedName~ResolveStatePayloadReferentMetadata_|"
    "FullyQualifiedName~GuardedStatePayloadWriteScopeTests"
)
if args.provider == "Windows":
    selected_tests = f"({selected_tests})&FullyQualifiedName!~ScopedWrite_PostPublicationReplayStillRejectsChangedIntermediateAlias"
subprocess.run([
    "dotnet", "test", "test/Nuplane.Store.Tests/Nuplane.Store.Tests.csproj",
    "--configuration", "Release",
    "--filter", selected_tests,
    "--logger", f"trx;LogFileName={trx.name}",
    "--results-directory", str(args.results_directory),
], check=True)
subprocess.run([
    sys.executable, "build/verify-required-trx.py", str(trx),
    f"build/test-cases/state-files-{args.provider.lower()}.json",
], check=True)
