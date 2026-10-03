@echo off
setlocal
call "D:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cd /d "D:\Projects\ADOFAIMacro\tools\abtest"

if not exist bin mkdir bin
for %%D in (base head v_deadonly v_nowindow v_plain v_dz05 v_base001) do if not exist obj\%%D mkdir obj\%%D

echo === base (2669a94) ===
cl /nologo /std:c++17 /EHsc /O2 /MT /DNDEBUG /utf-8 ^
   /Ibase /Foobj\base\ /Feobj\base\ ^
   base\TechniqueSimulator.cpp ^
   /link /DLL /OUT:bin\base.dll /DEF:base\TechniqueSimulator.def /MACHINE:X64 ole32.lib
if errorlevel 1 goto :fail

echo === head (B+C) ===
cl /nologo /std:c++17 /EHsc /O2 /MT /DNDEBUG /utf-8 ^
   /Ihead /Foobj\head\ /Feobj\head\ ^
   head\TechniqueSimulator.cpp ^
   /link /DLL /OUT:bin\head.dll /DEF:head\TechniqueSimulator.def /MACHINE:X64 ole32.lib
if errorlevel 1 goto :fail

echo === v_deadonly (C only, deadZone=0) ===
cl /nologo /std:c++17 /EHsc /O2 /MT /DNDEBUG /utf-8 ^
   /Iv_deadonly /Foobj\v_deadonly\ /Feobj\v_deadonly\ ^
   v_deadonly\TechniqueSimulator.cpp ^
   /link /DLL /OUT:bin\v_deadonly.dll /DEF:v_deadonly\TechniqueSimulator.def /MACHINE:X64 ole32.lib
if errorlevel 1 goto :fail

echo === v_nowindow (B only) ===
cl /nologo /std:c++17 /EHsc /O2 /MT /DNDEBUG /utf-8 ^
   /Iv_nowindow /Foobj\v_nowindow\ /Feobj\v_nowindow\ ^
   v_nowindow\TechniqueSimulator.cpp ^
   /link /DLL /OUT:bin\v_nowindow.dll /DEF:v_nowindow\TechniqueSimulator.def /MACHINE:X64 ole32.lib
if errorlevel 1 goto :fail

echo === v_plain (B+C off, closure test) ===
cl /nologo /std:c++17 /EHsc /O2 /MT /DNDEBUG /utf-8 ^
   /Iv_plain /Foobj\v_plain\ /Feobj\v_plain\ ^
   v_plain\TechniqueSimulator.cpp ^
   /link /DLL /OUT:bin\v_plain.dll /DEF:v_plain\TechniqueSimulator.def /MACHINE:X64 ole32.lib
if errorlevel 1 goto :fail

echo === v_dz05 (deadZone default 0.05) ===
cl /nologo /std:c++17 /EHsc /O2 /MT /DNDEBUG /utf-8 ^
   /Iv_dz05 /Foobj\v_dz05\ /Feobj\v_dz05\ ^
   v_dz05\TechniqueSimulator.cpp ^
   /link /DLL /OUT:bin\v_dz05.dll /DEF:v_dz05\TechniqueSimulator.def /MACHINE:X64 ole32.lib
if errorlevel 1 goto :fail

echo === v_base001 (base + 0.1%% jitter) ===
cl /nologo /std:c++17 /EHsc /O2 /MT /DNDEBUG /utf-8 ^
   /Iv_base001 /Foobj\v_base001\ /Feobj\v_base001\ ^
   v_base001\TechniqueSimulator.cpp ^
   /link /DLL /OUT:bin\v_base001.dll /DEF:v_base001\TechniqueSimulator.def /MACHINE:X64 ole32.lib
if errorlevel 1 goto :fail

echo BUILD_OK
exit /b 0
:fail
echo BUILD_FAIL
exit /b 1
