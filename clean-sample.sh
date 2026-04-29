#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SAMPLES_DIR="$SCRIPT_DIR/samples"

if [ ! -d "$SAMPLES_DIR" ]; then
    echo "samples 디렉터리를 찾을 수 없습니다: $SAMPLES_DIR" >&2
    exit 1
fi

find "$SAMPLES_DIR" -mindepth 1 -maxdepth 1 ! -name 'PRD.md' -exec rm -rf {} +

echo "samples 정리 완료 (PRD.md 제외)."
