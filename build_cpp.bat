@echo off
rem ==============================================================
rem  Build the two native x64 projects (Debug + Release):
rem    TechniqueSimulator\  technique simulation algorithm
rem    InputSystem\          NT kernel input injection
rem  Both sit next to the managed project ADOFAIMacro\ at the
rem  repo root - a vcxproj cannot live inside an SDK-style
rem  managed project's directory tree.
rem
rem  NOTE: keep this file ASCII-only. Non-ASCII comments make
rem  cmd.exe misparse it depending on the code page.
rem ==============================================================

call "D:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1

set MSB=D:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe
set ROOT=%~dp0

"%MSB%" "%ROOT%TechniqueSimulator\TechniqueSimulator.vcxproj" /p:Configuration=Debug   /p:Platform=x64 /v:minimal /nologo || exit /b 1
"%MSB%" "%ROOT%TechniqueSimulator\TechniqueSimulator.vcxproj" /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo || exit /b 1
"%MSB%" "%ROOT%InputSystem\InputSystem.vcxproj"             /p:Configuration=Debug   /p:Platform=x64 /v:minimal /nologo || exit /b 1
"%MSB%" "%ROOT%InputSystem\InputSystem.vcxproj"             /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo || exit /b 1

exit /b 0
