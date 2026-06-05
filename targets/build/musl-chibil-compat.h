/* chibil compat shim for compiling musl SOURCE to MSIL (force-included with
 * -include). Companion to the shadow arch headers in musl-compat/.
 *
 * Pull in musl's real src/include/features.h FIRST — it sets the FEATURES_H
 * guard and defines weak/hidden/weak_alias. Then override weak_alias: chibil has
 * no __attribute__((__alias__)), so we neutralize it for the compile spike (real
 * symbol aliasing — emitting `new` as a forwarder to `old` — is a link-time
 * concern, to be handled by chibil-link or a chibil __alias__ feature). Because
 * features.h is now guarded, later #include <features.h> from the TU are no-ops,
 * so this override persists. */
#include <features.h>

#undef weak_alias
#define weak_alias(old, new) /* dropped for compile-only spike */
