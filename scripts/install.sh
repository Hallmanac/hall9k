#!/usr/bin/env bash
# Hall9k bootstrap install (backlog 42): fetches the latest GitHub release for this
# platform (macOS arm64 or Linux x64), verifies its checksum, asks consent, and hands off
# to the release's own `h9k install --from-release` for the actual placement — binaries
# into ~/.hall9k/bin, h9k onto the PATH, the canonical skill set into ~/.hall9k/skills,
# Hall9k's own Postgres definition written (never started). Finishes by running
# `h9k doctor` so a fresh machine ends setup knowing what still needs attention.
#
# Needs no repo checkout and no .NET SDK — only `gh`, authenticated against the release's
# repository (a private repo's releases need `gh auth login` first), `tar`, and one of
# `sha256sum` / `shasum`.
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/Hallmanac/hall9k/main/scripts/install.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/Hallmanac/hall9k/main/scripts/install.sh | bash -s -- --yes
#
# Env overrides: HALL9K_REPO (owner/repo, default Hallmanac/hall9k), HALL9K_RELEASE_TAG
# (default: the release gh calls latest).

set -euo pipefail

REPO="${HALL9K_REPO:-Hallmanac/hall9k}"
TAG="${HALL9K_RELEASE_TAG:-}"
ASSUME_YES=0

for arg in "$@"; do
  case "$arg" in
    -y|--yes) ASSUME_YES=1 ;;
    *)
      echo "Unknown argument: $arg" >&2
      exit 64
      ;;
  esac
done

fail() {
  echo "hall9k install: $1" >&2
  exit 1
}

command -v gh >/dev/null 2>&1 || fail "gh (the GitHub CLI) is required — install it from https://cli.github.com, then gh auth login."
gh auth status >/dev/null 2>&1 || fail "gh is not authenticated — run gh auth login first (needed to read this repository's releases)."
command -v tar >/dev/null 2>&1 || fail "tar is required and was not found on PATH."

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) RID="osx-arm64" ;;
  Linux-x86_64) RID="linux-x64" ;;
  *)
    fail "no release for $(uname -s) $(uname -m) — release.yml builds osx-arm64 and linux-x64 only (Windows: scripts/install.ps1)."
    ;;
esac

ARCHIVE="hall9k-${RID}.tar.gz"
WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

echo "Fetching the latest release for ${RID} from ${REPO}…"
if [ -n "$TAG" ]; then
  gh release download "$TAG" --repo "$REPO" --pattern "$ARCHIVE" --pattern "checksums.txt" --dir "$WORKDIR" --clobber \
    || fail "gh release download failed for tag $TAG."
else
  gh release download --repo "$REPO" --pattern "$ARCHIVE" --pattern "checksums.txt" --dir "$WORKDIR" --clobber \
    || fail "gh release download failed — check gh auth status and that $REPO has a release carrying $ARCHIVE."
fi

[ -f "$WORKDIR/$ARCHIVE" ] || fail "$ARCHIVE was not among the downloaded release assets."
[ -f "$WORKDIR/checksums.txt" ] || fail "checksums.txt was not among the downloaded release assets — cannot verify $ARCHIVE."

echo "Verifying checksum…"
# sha256sum's own two grammars: "<hex>  <file>" (text mode) and "<hex> *<file>" (binary
# mode). Splitting on whitespace and trimming a leading '*' off the file field accepts
# both, matching install.ps1 and ReleaseArchive.VerifyAsync (h9k update) on the .NET side.
EXPECTED="$(awk -v name="$ARCHIVE" '{ file = $2; sub(/^\*/, "", file); if (file == name) { print $1; exit } }' "$WORKDIR/checksums.txt")"
[ -n "$EXPECTED" ] || fail "$ARCHIVE has no entry in checksums.txt."
if command -v sha256sum >/dev/null 2>&1; then
  ACTUAL="$(sha256sum "$WORKDIR/$ARCHIVE" | awk '{print $1}')"
else
  ACTUAL="$(shasum -a 256 "$WORKDIR/$ARCHIVE" | awk '{print $1}')"
fi
[ "$EXPECTED" = "$ACTUAL" ] || fail "checksum mismatch for $ARCHIVE (expected $EXPECTED, got $ACTUAL) — refusing to install a payload that does not match what was published."
echo "Checksum verified."

if [ "$ASSUME_YES" -ne 1 ]; then
  PROMPT="This will place h9k and h9kd in ~/.hall9k/bin, add h9k to your PATH, and publish Hall9k's skills to ~/.hall9k/skills. Nothing is started or registered as a background service. Continue? [y/N] "
  # A curl-pipe leaves stdin consumed by the pipe, so the prompt is read from the
  # controlling terminal directly when one exists, the same trick well-behaved
  # curl-to-shell installers use.
  if [ -r /dev/tty ]; then
    read -r -p "$PROMPT" REPLY </dev/tty || REPLY=""
  else
    read -r -p "$PROMPT" REPLY || REPLY=""
  fi
  case "$REPLY" in
    y|Y|yes|YES) ;;
    *) fail "Cancelled — nothing was changed. Re-run with --yes to skip this prompt." ;;
  esac
fi

echo "Unpacking…"
PAYLOAD="$WORKDIR/payload"
mkdir -p "$PAYLOAD"
tar -xzf "$WORKDIR/$ARCHIVE" -C "$PAYLOAD"
chmod +x "$PAYLOAD/h9k" "$PAYLOAD/h9kd" 2>/dev/null || true

echo "Installing…"
"$PAYLOAD/h9k" install --from-release "$PAYLOAD" --no-restart

# Re-resolve h9k for the doctor run: install may have just put it on the PATH for the
# first time, which this already-running shell has not picked up yet.
H9K="$HOME/.hall9k/bin/h9k"
[ -x "$H9K" ] || H9K="$PAYLOAD/h9k"

echo
echo "Running h9k doctor…"
"$H9K" doctor || true

echo
if command -v h9k >/dev/null 2>&1; then
  echo "Done. h9k is on your PATH."
else
  echo "Done. h9k did not land on a directory already on your PATH — see the warning above, or run it directly at $H9K."
fi
