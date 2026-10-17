@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0..\.."
python tools\battle-scale\prepare_segmented_lookup_harness.py
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O2 tests\AgentStorage\native_lookup_harness.cpp /Fo:build\agent-storage\native_lookup_harness.obj /Fe:build\agent-storage\native_lookup_harness.exe
if errorlevel 1 exit /b %errorlevel%
build\agent-storage\native_lookup_harness.exe
set "test_result=%errorlevel%"
popd
exit /b %test_result%
