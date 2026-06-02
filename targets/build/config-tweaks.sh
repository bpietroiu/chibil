#!/bin/sh
# Apply the two config.h changes the chibil/CoreCLR bring-up needs, AFTER running
# bash's ./configure (config.h is generated, so it can't be patched). Run from the
# bash source root.
#
#   USING_BASH_MALLOC — bash's bundled sbrk allocator fights the .NET runtime heap
#                       (every allocation fails); route malloc/free to the host libc.
#   HAVE_ARC4RANDOM   — absent in the target libc; bash falls back to getrandom.
set -e
[ -f config.h ] || { echo "config-tweaks.sh: run from the bash source root after ./configure" >&2; exit 1; }
sed -i \
  -e 's|^#define USING_BASH_MALLOC 1|/* #define USING_BASH_MALLOC 1 (disabled for chibil) */|' \
  -e 's|^#define HAVE_ARC4RANDOM 1|/* #define HAVE_ARC4RANDOM 1 (disabled for chibil) */|' \
  config.h
echo "config.h: USING_BASH_MALLOC and HAVE_ARC4RANDOM disabled."
