/* bash-shim.c — link tail for running GNU bash on SandboxPal (managed musl).
 *
 * The native bash bring-up linked the host glibc (libc.so.6) + libtinfo, which
 * supplied a long tail of symbols the managed-musl object set deliberately omits
 * (networking, passwd/group DB, dlopen, regex, termcap, …). bash's non-interactive
 * `-c '<script>'` path never exercises any of them, so these are stub definitions
 * whose only job is to make the SandboxPal link self-contained (zero native deps).
 *
 * Compiled against the SAME musl headers bash was compiled against, so every stub
 * has the exact signature the call sites expect (clean cross-TU binding). The list
 * was derived empirically: `chibil-link … -lc --print-imports` over the bash image
 * reports precisely these 38 symbols (33 func + 5 data) as otherwise-unresolved.
 *
 * Each behaves as "feature absent": failure return / NULL / empty, which is the
 * correct answer in a sandbox with no terminal, no network, and no host user DB.
 */

#define _GNU_SOURCE
#include <stddef.h>

/* ── termcap / terminfo (readline line editing) ───────────────────────────── */
int tgetent(char *bp, const char *name) { (void)bp; (void)name; return 0; }
int tgetflag(const char *id) { (void)id; return 0; }
int tgetnum(const char *id) { (void)id; return -1; }
char *tgetstr(const char *id, char **area) { (void)id; (void)area; return 0; }
char *tgoto(const char *cap, int col, int row) { (void)cap; (void)col; (void)row; return 0; }
int tputs(const char *str, int affcnt, int (*pc)(int)) { (void)str; (void)affcnt; (void)pc; return 0; }
char  PC = 0;
char *BC = 0;
char *UP = 0;
short ospeed = 0;

/* ── terminal attributes (src/termios — not in the managed set) ───────────── */
#include <termios.h>
int tcgetattr(int fd, struct termios *t) { (void)fd; (void)t; return -1; }  /* not a tty */
int tcflow(int fd, int action) { (void)fd; (void)action; return 0; }

/* ── passwd / group DB (src/passwd — host user DB not exposed to the sandbox) ─ */
#include <pwd.h>
#include <grp.h>
struct passwd *getpwnam(const char *name) { (void)name; return 0; }
struct passwd *getpwuid(uid_t uid) { (void)uid; return 0; }
struct passwd *getpwent(void) { return 0; }
void setpwent(void) { }
void endpwent(void) { }
struct group *getgrent(void) { return 0; }
void setgrent(void) { }
void endgrent(void) { }

/* ── dynamic loading (no dlopen in a statically-merged image) ──────────────── */
void *dlopen(const char *file, int mode) { (void)file; (void)mode; return 0; }
void *dlsym(void *handle, const char *name) { (void)handle; (void)name; return 0; }
int   dlclose(void *handle) { (void)handle; return -1; }
char *dlerror(void) { return (char *)"dynamic loading not supported"; }

/* ── networking (src/network — no sockets in the managed PAL) ──────────────── */
#include <netdb.h>
#include <sys/socket.h>
int getaddrinfo(const char *node, const char *service,
                const struct addrinfo *hints, struct addrinfo **res)
{ (void)node; (void)service; (void)hints; if (res) *res = 0; return EAI_FAIL; }
void freeaddrinfo(struct addrinfo *res) { (void)res; }
const char *gai_strerror(int code) { (void)code; return "name resolution unavailable"; }
struct servent *getservent(void) { return 0; }
void setservent(int stayopen) { (void)stayopen; }
void endservent(void) { }
int getpeername(int fd, struct sockaddr *addr, socklen_t *len) { (void)fd; (void)addr; (void)len; return -1; }
#include <netinet/in.h>
const struct in6_addr in6addr_any = {{{0}}};
const struct in6_addr in6addr_loopback = {{{0}}};

/* ── regular expressions (src/regex — not in the managed set) ──────────────── */
#include <regex.h>
int regcomp(regex_t *preg, const char *pat, int cflags) { (void)preg; (void)pat; (void)cflags; return REG_BADPAT; }
int regexec(const regex_t *preg, const char *str, size_t n, regmatch_t pmatch[], int eflags)
{ (void)preg; (void)str; (void)n; (void)pmatch; (void)eflags; return REG_NOMATCH; }
size_t regerror(int e, const regex_t *preg, char *buf, size_t size)
{ (void)e; (void)preg; if (buf && size) buf[0] = 0; return 0; }
void regfree(regex_t *preg) { (void)preg; }

/* ── filename matching (src — fnmatch not in the managed set) ──────────────── */
#include <fnmatch.h>
int fnmatch(const char *pat, const char *str, int flags) { (void)pat; (void)str; (void)flags; return FNM_NOMATCH; }

/* ── temp files (src/temp — sandbox has no host temp dir surface) ──────────── */
#include <stdlib.h>
#include <unistd.h>
int   mkstemp(char *tmpl) { (void)tmpl; return -1; }
char *mkdtemp(char *tmpl) { (void)tmpl; return 0; }
char *mktemp(char *tmpl) { return tmpl; }

/* ── misc conf / access (src/conf, src/legacy — not in the managed set) ───── */
size_t confstr(int name, char *buf, size_t len) { (void)name; if (buf && len) buf[0] = 0; return 0; }
long pathconf(const char *path, int name) { (void)path; (void)name; return -1; }
int getdtablesize(void) { return 1024; }
int eaccess(const char *path, int mode) { (void)path; (void)mode; return -1; }

/* ── managed-crt startup ──────────────────────────────────────────────────────
 * The synthesized entry point calls C main() DIRECTLY, bypassing musl's
 * __libc_start_main/__init_libc, so the per-thread TLS state musl normally sets up
 * is never initialized. GreenProcess invokes this once on the main thread before
 * main(). It (1) builds the minimal auxv mallocng needs (pal-shim's __chibil_pal_init)
 * and (2) points the thread's locale at the global C locale — without it the first
 * CURRENT_LOCALE deref (e.g. __ctype_get_mb_cur_max in setlocale) null-faults.
 * (Lives here, not pal-shim.c: pal-shim defines __wake/__wait stubs that collide with
 * pthread_impl.h's inlines, which we need for __pthread_self / __libc.) */
#include "pthread_impl.h"
extern void __chibil_pal_init(void);
void __chibil_rt_init(void)
{
    __chibil_pal_init();
    __pthread_self()->locale = &__libc.global_locale;
    /* musl's exit() uses `__pthread_self()->tid` as the exit-lock owner and calls
     * a_crash() when a_cas sees the lock already holds our tid. A zeroed TCB has
     * tid==0, which matches the lock's initial 0 → spurious "recursive exit" crash.
     * Any nonzero tid breaks that false match. */
    __pthread_self()->tid = 1;
}

/* ── data symbols ─────────────────────────────────────────────────────────── */
/* bash references a bare `errno` in the (rare) files where musl's errno macro
 * isn't in scope; musl exposes only __errno_location, so provide storage. NOTE:
 * decoupled from musl's TLS errno — a limitation, but the `-c` happy path doesn't
 * read it. pthread_impl.h (above) pulls <errno.h>, so drop the macro first. */
#undef errno
int errno = 0;
/* HISTORY flag gated out of the generated y.tab.c; default 0 = "no leading-# comment". */
int current_command_first_line_comment = 0;
/* readline macro-execution pointer (interactive only). */
char *_rl_executing_macro = 0;
