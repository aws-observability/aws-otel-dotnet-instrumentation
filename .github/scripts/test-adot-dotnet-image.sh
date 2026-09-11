#!/bin/bash
# Post-build verification for the ADOT .NET auto-instrumentation image (Linux). Modeled on the
# Python/JS verifiers. Two checks:
#   - Copy fidelity: the image's own cp-utility copies the baked-in /autoinstrumentation payload
#     into an operator volume (as the OTel Operator's init container does), and that copy is
#     byte-for-byte identical to the image payload (diff -r).
#   - Independent reference: the image payload matches the main-build artifact it was built from
#     (the unzipped OpenTelemetryDistribution/<arch>, passed as REFERENCE_DIR). Best-effort:
#     skipped if the reference isn't provided/found; only a real mismatch fails.
#
# A load check (running the CLR profiler against the payload) is intentionally out of scope here
# and left as a follow-up -- .NET auto-instrumentation loads via native+managed assemblies and a
# dotnet runtime + CORECLR_*/OTEL_DOTNET_AUTO_HOME env, which is heavier and arch/libc-specific.
#
# Usage: test-adot-dotnet-image.sh <TEST_TAG> [REFERENCE_DIR]
#   TEST_TAG       image ref to test (a locally built, not-yet-pushed image)
#   REFERENCE_DIR  optional path to the unzipped main-build artifact (OpenTelemetryDistribution/<arch>);
#                  when set, the image payload is diffed against it.

set -x -e -u

TEST_TAG=$1
REFERENCE_DIR="${2:-}"

# Per-run unique names so a run killed before the trap fires can't leave a volume/containers
# behind for the next run to pick up -- which would make diff -r compare a mixed tree.
RUN_ID="$$-${RANDOM}"
VOLUME="operator-volume-${RUN_ID}"
VERIFY_CTR="adot-verify-${RUN_ID}"
SRC_CTR="adot-src-${RUN_ID}"
NEUTRAL_IMAGE="public.ecr.aws/docker/library/alpine:3"
WORKDIR=$(mktemp -d)
IMAGE_SRC="${WORKDIR}/image-src"
VOLUME_COPY="${WORKDIR}/volume-copy"

cleanup() {
  docker rm -f "${VERIFY_CTR}" >/dev/null 2>&1 || true
  docker rm -f "${SRC_CTR}" >/dev/null 2>&1 || true
  docker volume rm "${VOLUME}" >/dev/null 2>&1 || true
  rm -rf "${WORKDIR}"
}
trap cleanup EXIT

docker volume create "${VOLUME}"

# Extract the image's baked-in payload up front (scratch image: create, don't run) -- used for
# both the copy-fidelity diff and the independent-reference diff.
docker create --name "${SRC_CTR}" "${TEST_TAG}" /bin/cp >/dev/null
docker cp "${SRC_CTR}":/autoinstrumentation "${IMAGE_SRC}"

# 1. Exercise the image's own cp-utility exactly as the operator init container does:
#    recursively copy the baked-in /autoinstrumentation payload into the shared volume.
docker run --rm --mount source="${VOLUME}",dst=/otel-auto-instrumentation "${TEST_TAG}" \
  /bin/cp -r /autoinstrumentation /otel-auto-instrumentation

# 2. Assert the payload actually landed in the operator volume, using a neutral container
#    (the ADOT image is FROM scratch and has no shell/coreutils).
docker run -d --name "${VERIFY_CTR}" --mount source="${VOLUME}",dst=/otel-auto-instrumentation \
  "${NEUTRAL_IMAGE}" sleep 300 >/dev/null
docker cp "${VERIFY_CTR}":/otel-auto-instrumentation "${VOLUME_COPY}"
if [ -z "$(ls -A "${VOLUME_COPY}" 2>/dev/null)" ]; then
  echo "error: /autoinstrumentation was not copied into the operator-volume"
  exit 1
fi
echo "autoinstrumentation payload was copied to the operator-volume"

# 3. Copy fidelity: the copied tree must be byte-for-byte identical to the image payload.
if diff -r "${IMAGE_SRC}" "${VOLUME_COPY}"; then
  echo "copied autoinstrumentation payload matched the image payload"
else
  echo "error: copied autoinstrumentation payload differs from the image payload"
  exit 1
fi

# 4. Independent reference (optional): the image payload must match the main-build artifact it was
#    built from (the unzipped OpenTelemetryDistribution/<arch>). Best-effort -- warn and SKIP if
#    the reference isn't provided/found so reference acquisition never blocks a release; only a
#    real content mismatch fails.
if [ -n "${REFERENCE_DIR}" ]; then
  if [ ! -d "${REFERENCE_DIR}" ]; then
    echo "warning: reference dir '${REFERENCE_DIR}' not found; skipping independent-reference check"
  elif diff -r "${REFERENCE_DIR}" "${IMAGE_SRC}"; then
    echo "image payload matched the main-build artifact (${REFERENCE_DIR})"
  else
    echo "error: image payload differs from the main-build artifact (${REFERENCE_DIR})"
    exit 1
  fi
fi
