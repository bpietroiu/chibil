#!/usr/bin/env bash
# samples/sqlite/fetch-amalgamation.sh
# Pinned SQLite amalgamation. If this URL 404s, bump to a current release
# from https://sqlite.org/download.html and update VERSION.
set -euo pipefail

VERSION='3470200'   # SQLite 3.47.2
url="https://www.sqlite.org/2024/sqlite-amalgamation-${VERSION}.zip"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
vendor="${here}/vendor"
mkdir -p "${vendor}"

zip="/tmp/sqlite-${VERSION}.zip"
tmp="/tmp/sqlite-${VERSION}"

curl -L -o "${zip}" "${url}"
rm -rf "${tmp}"
mkdir -p "${tmp}"
unzip -q "${zip}" -d "${tmp}"

src="$(find "${tmp}" -name sqlite3.c | head -n1)"
hdr="$(find "${tmp}" -name sqlite3.h | head -n1)"
cp "${src}" "${vendor}/sqlite3.c"
cp "${hdr}" "${vendor}/sqlite3.h"

echo "vendored sqlite ${VERSION} -> ${vendor}"
