// Behavioral oracle: evaluate JavaScript through the chibil-built `quickjs.Api` facade —
// no reflection, no hand-written P/Invoke. Proves a C# program consumes a C library that
// chibil compiled to MSIL, using real types from the public header.
using System;
using System.Text;
using quickjs;

unsafe
{
    JSRuntime* rt = Api.JS_NewRuntime();
    if (rt == null) { Console.Error.WriteLine("JS_NewRuntime returned null"); Environment.Exit(2); }
    JSContext* ctx = Api.JS_NewContext(rt);
    if (ctx == null) { Console.Error.WriteLine("JS_NewContext returned null"); Environment.Exit(2); }

    byte[] code = Encoding.ASCII.GetBytes("40+2");
    byte[] file = Encoding.ASCII.GetBytes("<oracle>\0");
    int result;
    fixed (byte* c = code)
    fixed (byte* f = file)
    {
        // JS_Eval(ctx, input, input_len, filename, eval_flags=0 /* JS_EVAL_TYPE_GLOBAL */)
        JSValue v = Api.JS_Eval(ctx, (sbyte*)c, (ulong)code.Length, (sbyte*)f, 0);
        int outv;
        Api.JS_ToInt32(ctx, &outv, v);
        result = outv;
        // Also read the nested-aggregate field directly (proves Plan 3.5 JSValue.u is usable).
        Console.WriteLine($"JS_ToInt32={outv}  JSValue.u.int32={v.u.int32}  tag={v.tag}");
    }

    Api.JS_FreeContext(ctx);
    Api.JS_FreeRuntime(rt);

    if (result != 42) { Console.Error.WriteLine($"ORACLE_FAIL expected 42 got {result}"); Environment.Exit(1); }
    Console.WriteLine("BEHAVIORAL_ORACLE_OK: quickjs.Api.JS_Eval(\"40+2\") == 42");
}
