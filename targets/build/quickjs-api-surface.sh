#!/bin/bash
# Surface oracle: assert qjs.dll's `quickjs` facade exposes the real public API.
# Run after quickjs-api.sh. WSL: wsl bash /mnt/d/sandbox/chibil/targets/build/quickjs-api-surface.sh
set -u
ROOT=/mnt/d/sandbox/chibil
QJS=$ROOT/targets/quickjs-2025-09-13/qjs.dll
[ -f "$QJS" ] || { echo "qjs.dll missing — run quickjs-api.sh first"; exit 1; }
D=$(mktemp -d)
cat > "$D/Program.cs" <<'CS'
using System;using System.Linq;using System.Reflection;
class P{static int Main(){
 var rtDir=System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
 var asms=System.IO.Directory.GetFiles(rtDir,"*.dll").ToList();
 string qjs=Environment.GetEnvironmentVariable("QJS");
 asms.Add(qjs);
 var mlc=new MetadataLoadContext(new PathAssemblyResolver(asms));
 var asm=mlc.LoadFromAssemblyPath(qjs);
 var api=asm.GetType("quickjs.Api");
 int funcs=api.GetMethods(BindingFlags.Public|BindingFlags.Static).Length;
 string[] needType={"quickjs.JSValue","quickjs.JSValueUnion","quickjs.JSRuntime","quickjs.JSContext"};
 string[] needFunc={"JS_NewRuntime","JS_NewContext","JS_Eval","JS_ToInt32"};
 int enums=asm.GetTypes().Count(t=>t.Namespace=="quickjs"&&t.IsEnum);
 bool ok=funcs>=150;
 Console.WriteLine($"facade functions={funcs} (>=150: {funcs>=150})");
 foreach(var t in needType){bool p=asm.GetType(t)!=null;ok&=p;Console.WriteLine($"type {t}: {p}");}
 foreach(var f in needFunc){bool p=api.GetMethod(f,BindingFlags.Public|BindingFlags.Static)!=null;ok&=p;Console.WriteLine($"func {f}: {p}");}
 ok&=enums>=3;Console.WriteLine($"enums={enums} (>=3: {enums>=3})");
 Console.WriteLine(ok?"SURFACE_ORACLE_OK":"SURFACE_ORACLE_FAIL");
 return ok?0:1;
}}
CS
cat > "$D/s.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>
<TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup>
<ItemGroup><PackageReference Include="System.Reflection.MetadataLoadContext" Version="9.0.0"/></ItemGroup></Project>
CSPROJ
QJS="$QJS" dotnet run --project "$D" 2>&1 | tail -20
rc=${PIPESTATUS[0]}
rm -rf "$D"
exit $rc
