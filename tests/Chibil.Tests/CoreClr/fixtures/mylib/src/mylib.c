#include "mylib.h"
static int secret(int x){ return x * 2; }
int ml_add(int a, int b){ return a + b + secret(0); }
