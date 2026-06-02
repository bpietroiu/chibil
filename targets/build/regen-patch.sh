#!/bin/bash
# Regenerate chibil-bash-5.3.patch from a modified bash source tree, by diffing
# the chibil-touched files against a pristine bash 5.3.
#
#   ./regen-patch.sh [MODIFIED_BASH_SRC_DIR]
#
# MODIFIED_BASH_SRC_DIR defaults to ../bash-5.3 (the tree you built from). The
# pristine reference is the bash-5.3 release tarball (cached at $BASH_TARBALL,
# default /tmp/bash-5.3.tar.gz; downloaded if absent).
set -e
git config --global --add safe.directory '*' 2>/dev/null || true
HERE=$(cd "$(dirname "$0")" && pwd)
SRC=${1:-"$HERE/../bash-5.3"}
FILES="jobs.c jobs.h execute_cmd.c subst.c variables.c variables.h"
TARBALL=${BASH_TARBALL:-/tmp/bash-5.3.tar.gz}

[ -d "$SRC" ] || { echo "regen-patch: no modified tree at $SRC" >&2; exit 1; }
[ -f "$TARBALL" ] || wget -q https://ftp.gnu.org/gnu/bash/bash-5.3.tar.gz -O "$TARBALL"

T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
tar xf "$TARBALL" -C "$T"
cd "$T/bash-5.3"
git init -q && git config user.email x@x && git config user.name x
git add -A && git commit -qm pristine
for f in $FILES; do cp "$SRC/$f" "$f"; done
git diff > "$HERE/chibil-bash-5.3.patch"
echo "wrote $HERE/chibil-bash-5.3.patch"
git diff --stat
