/* chibil compat shim for compiling musl SOURCE to MSIL (force-included with
 * -include). Companion to the shadow arch headers in musl-compat/.
 *
 * musl's real weak_alias (`extern __typeof(old) new __attribute__((weak,
 * alias(#old)))`) is now supported by chibil: __typeof of a function declares a
 * function-typed symbol, and __attribute__((alias("old"))) emits an alias that
 * chibil-link binds to old's token. So we no longer neutralize it — including
 * <features.h> for the weak/hidden macros is enough. */
#include <features.h>
