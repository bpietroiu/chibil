#ifndef MYLIB_H
#define MYLIB_H
struct MlPoint { int x; int y; };
struct MlCtx;
int ml_add(int a, int b);
int ml_sum(struct MlPoint p);
struct MlCtx *ml_ctx_new(void);
int ml_ctx_id(struct MlCtx *c);
enum MlColor { ML_RED, ML_GREEN = 5, ML_BLUE };
int ml_color_code(enum MlColor c);
enum MlColor ml_default_color(void);
struct MlInner { int a; int b; };
struct MlOuter { struct MlInner inner; int tag; };
int ml_outer_sum(struct MlOuter o);
#endif
