/* Layer 1: pure musl (strlen) + a main that calls it. No PAL needed. Expect 5. */
typedef unsigned long size_t;
size_t strlen(const char *);
int main(void) { return (int)strlen("hello"); }
