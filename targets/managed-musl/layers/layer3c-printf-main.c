/* Layer 3c: printf to stdout on the managed PAL (stdio FILE + writev). Expect exit 0. */
int printf(const char *, ...);
extern void __chibil_pal_init(void);
int main(void) {
    __chibil_pal_init();
    printf("hello from managed musl printf: %d + %d = %d\n", 40, 2, 42);
    return 0;
}
