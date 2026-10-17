@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0..\.."
if not exist build\element-identity-diagnostic mkdir build\element-identity-diagnostic
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O2 /LD src\MNBL.AgentGrowthProbe\NativeIdentityDiagnostic.cpp bcrypt.lib /Fo:build\element-identity-diagnostic\NativeIdentityDiagnostic.obj /Fe:build\element-identity-diagnostic\MNBL.NativeElementProbe.dll
set "test_result=%errorlevel%"
popd
exit /b %test_result%
