#ifndef MYLIB_H
#define MYLIB_H
struct MlPoint { int x; int y; };
struct MlCtx;
int ml_add(int a, int b);
int ml_sum(struct MlPoint p);
struct MlCtx *ml_ctx_new(void);
int ml_ctx_id(struct MlCtx *c);
#endif
