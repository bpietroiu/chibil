#!/bin/bash
cd /mnt/d/sandbox/chibil/targets/quickjs-2025-09-13 || exit 1
cat > /tmp/probe.js <<'JS'
function t(name, fn){ try { print(name + ": " + fn()); } catch(e){ print(name + ": THROW " + e); } }
t("exceptions",   () => { try { throw new Error("boom"); } catch(e){ return e.message; } });
t("finally",      () => { let r=""; try { throw 1; } catch(e){ r+="c"; } finally { r+="f"; } return r; });
t("closures",     () => { let c=0; const f=()=>++c; f(); return f(); });
t("classes",      () => { class A{ constructor(){this.x=5;} get d(){return this.x*2;} } return new A().d; });
t("inheritance",  () => { class A{m(){return 1;}} class B extends A{m(){return super.m()+1;}} return new B().m(); });
t("generators",   () => { function* g(){ yield 1; yield 2; yield 3; } let s=0; for(const v of g()) s+=v; return s; });
t("destructuring",() => { const [a,b]=[1,2]; const {x}={x:3}; return a+b+x; });
t("spread",       () => Math.max(...[1,5,3]));
t("template",     () => { const n=7; return `n=${n*2}`; });
t("regexp",       () => "a1b2c3".replace(/\d/g, "#"));
t("regexp-named", () => { const m = /(?<y>\d{4})/.exec("2026"); return m.groups.y; });
t("map",          () => { const m=new Map([["a",1],["b",2]]); return m.get("a")+m.get("b"); });
t("set",          () => new Set([1,1,2,3,3]).size);
t("typedarray",   () => { const a=new Int32Array([1,2,3]); return a[0]+a[2]; });
t("bigint",       () => (2n ** 64n).toString());
t("json",         () => JSON.parse('{"a":[1,2,3]}').a[2]);
t("symbol",       () => { const s=Symbol("k"); const o={[s]:7}; return o[s]; });
t("proxy",        () => { const p=new Proxy({}, {get:()=>42}); return p.anything; });
t("reduce",       () => [1,2,3,4].reduce((a,b)=>a+b, 0));
t("sort",         () => [5,3,8,1].sort((a,b)=>a-b).join(","));
Promise.resolve(40).then(v => print("promise-then: " + (v+2)));
(async () => { try { const v = 1 + await Promise.resolve(2); print("async-await: " + v); } catch(e){ print("async: THROW "+e); } })();
JS
timeout 30 dotnet qjs.dll /tmp/probe.js 2>&1 | head -40
echo "exit=${PIPESTATUS[0]}"
