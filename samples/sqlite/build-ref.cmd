@echo off
setlocal
set HERE=%~dp0
cl /nologo /DSQLITE_REF_BUILD /Fe:"%HERE%ref.exe" /I"%HERE%vendor" /FI"%HERE%sqlite_cfg.h" ^
   "%HERE%vendor\sqlite3.c" "%HERE%sqlite_shim.c" "%HERE%main.c"
if errorlevel 1 ( echo REFERENCE BUILD FAILED & exit /b 1 )
echo built %HERE%ref.exe
