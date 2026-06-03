#include "mylib.h"
static int secret(int x){ return x * 2; }
int ml_add(int a, int b){ return a + b + secret(0); }
int ml_sum(struct MlPoint p){ return p.x + p.y; }
struct MlCtx *ml_ctx_new(void){ return (struct MlCtx*)0; }
int ml_ctx_id(struct MlCtx *c){ return c ? 1 : 0; }
int ml_color_code(enum MlColor c){ return (int)c + 100; }
enum MlColor ml_default_color(void){ return ML_GREEN; }
