#!/usr/bin/env bash
set -euo pipefail

filesystem=${1:?Expected overlay, ext4, or xfs}
results=${2:?Expected an owned results directory}
mkdir -p "$results"
results=$(cd "$results" && pwd)
mount_args=()
container_root=/var/tmp/nuplane-qualification

case "$filesystem" in
  overlay) expected_magic=794c7630 ;;
  ext4|xfs)
    volume=$(mktemp -d)
    image=$(mktemp)
    trap 'if mountpoint -q "$volume"; then sudo umount "$volume"; fi; rmdir "$volume"; rm -f "$image"' EXIT
    truncate -s 512M "$image"
    if [[ $filesystem == xfs ]]; then
      sudo apt-get update -qq
      sudo apt-get install -y -qq xfsprogs
      expected_magic=58465342
    else
      expected_magic=ef53
    fi
    "mkfs.$filesystem" -q "$image"
    sudo mount -o loop "$image" "$volume"
    container_root=/nuplane-volume
    mount_args=(--mount "type=bind,src=$volume,dst=$container_root")
    ;;
  *) echo "Unsupported qualification filesystem: $filesystem" >&2; exit 2 ;;
esac

docker run --rm \
  --mount "type=bind,src=$PWD,dst=/source,readonly" \
  --mount "type=bind,src=$results,dst=/test-results" \
  "${mount_args[@]}" \
  --env "TMPDIR=$container_root" \
  --env "NUPLANE_EXPECT_FILESYSTEM_MAGIC=$expected_magic" \
  --env NUPLANE_COORDINATED_CONTAINER_TEST=1 \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
    set -euo pipefail
    mkdir -p /work "$TMPDIR"
    test "$(stat -f -c %t "$TMPDIR")" = "$NUPLANE_EXPECT_FILESYSTEM_MAGIC"
    cd /source
    tar --exclude=.git --exclude=bin --exclude=obj -cf - . | tar -C /work -xf -
    cd /work
    dotnet test test/Nuplane.Integration.Tests/Nuplane.Integration.Tests.csproj -c Release \
      --filter "FullyQualifiedName~Reconciliation_AcquiresMissingAuthenticatedPackage_NativelyProtectsAndLoadsExactGraph|FullyQualifiedName~CoordinatedNameProfile_ContainerTmpfs_IsRefusedWithFilesystemAndProbe" \
      --logger "trx;LogFileName=coordinated-store.trx" --results-directory /test-results
  '

python3 build/verify-required-trx.py "$results/coordinated-store.trx" \
  build/test-cases/coordinated-store-container.json
