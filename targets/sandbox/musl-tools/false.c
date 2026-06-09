/* false — managed coreutil (M5): always fails (exit 1). Pair to true, for exit-status
 * and `||` / `set -e` plumbing. */
int main(void) { return 1; }
