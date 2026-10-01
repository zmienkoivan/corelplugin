#!/bin/sh
set -eu

repo_dir="${PREVIEW_REPO_DIR:-/opt/vanya-preview/repo}"
site_dir="$repo_dir/preview-site"
state_dir="${PREVIEW_STATE_DIR:-/opt/vanya-preview}"

if [ ! -f "$state_dir/.env.production" ] || [ ! -d "$state_dir/data" ]; then
  echo "Preview settings or data directory is missing: $state_dir" >&2
  exit 1
fi

cd "$repo_dir"
git fetch origin main
if [ "$(git branch --show-current)" != main ]; then
  echo "Expected the main branch in $repo_dir" >&2
  exit 1
fi
git merge --ff-only origin/main

cd "$site_dir"
export PREVIEW_ENV_FILE="$state_dir/.env.production"
export PREVIEW_DATA_PATH="$state_dir/data"
docker compose -p vanya-preview -f compose.production.yaml up -d --build --no-deps preview
docker compose -p vanya-preview -f compose.production.yaml ps preview
