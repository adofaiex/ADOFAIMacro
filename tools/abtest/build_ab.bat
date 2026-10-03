@echo off
setlocal
call "D:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cd /d "D:\Projects\ADOFAIMacro\tools\abtest"

if not exist bin mkdir bin
if not exist obj\base mkdir obj\base
if not exist obj\head mkdir obj\head
if not exist obj\ab mkdir obj\ab

echo === building base (2669a94) ===
cl /nologo /std:c++17 /EHsc /O2 /MT /DNDEBUG /utf-8 ^
   /Ibase /Foobj\base\ /Feobj\base\ ^
   base\TechniqueSimulator.cpp ^
   /link /DLL /OUT:bin\base.dll /DEF:base\TechniqueSimulator.def /MACHINE:X64 ole32.lib
if errorlevel 1 goto :fail

echo === building head (working tree) ===
cl /nologo /std:c++17 /EHsc /O2 /MT /DNDEBUG /utf-8 ^
   /Ihead /Foobj\head\ /Feobj\head\ ^
   head\TechniqueSimulator.cpp ^
   /link /DLL /OUT:bin\head.dll /DEF:head\TechniqueSimulator.def /MACHINE:X64 ole32.lib
if errorlevel 1 goto :fail

echo === building harness ===
cl /nologo /std:c++17 /EHsc /O2 /MT /DNDEBUG /utf-8 ^
   /Foobj\ab\ /Feobj\ab\ harness.cpp ^
   /link /OUT:bin\ab.exe /MACHINE:X64 ole32.lib
if errorlevel 1 goto :fail

echo BUILD_OK
exit /b 0
:fail
echo BUILD_FAIL
exit /b 1

