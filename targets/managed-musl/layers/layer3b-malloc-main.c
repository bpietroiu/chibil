/* Layer 3b: mallocng malloc()+free() over the managed PAL (mmap). Expect 42. */
typedef unsigned long size_t;
void *malloc(size_t); void free(void *);
extern void __chibil_pal_init(void);   /* managed-crt startup (auxv) */
int main(void) {
    __chibil_pal_init();
    char *p = (char*)malloc(100);
    for (int i = 0; i < 100; i++) p[i] = (char)i;
    int r = p[42];
    free(p);
    return r;            /* expect 42 */
}
