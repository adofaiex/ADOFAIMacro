@echo off
setlocal
call "D:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cd /d "D:\Projects\ADOFAIMacro\tools\abtest"

if not exist bin mkdir bin
if not exist obj\solver mkdir obj\solver

echo === building solver_ab harness ===
cl /nologo /std:c++17 /EHsc /O2 /MT /DNDEBUG /utf-8 ^
   /Foobj\solver\ /Feobj\solver\ solver_ab.cpp ^
   /link /OUT:bin\solver_ab.exe /MACHINE:X64 ole32.lib
if errorlevel 1 goto :fail

echo BUILD_OK
exit /b 0
:fail
echo BUILD_FAIL
exit /b 1