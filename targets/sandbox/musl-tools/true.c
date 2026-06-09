/* true — managed coreutil (M5): always succeeds. The trivial external, for exercising
 * exit-status plumbing (`true && echo y`, `set -e; true`). */
int main(void) { return 0; }
