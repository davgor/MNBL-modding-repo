@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0..\.."
python tools\battle-scale\prepare_segmented_scalar_harness.py
if errorlevel 1 goto failed
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O2 tests\AgentStorage\native_scalar_harness.cpp /Fo:build\agent-storage\native_scalar_harness.obj /Fe:build\agent-storage\native_scalar_harness.exe
if errorlevel 1 goto failed
build\agent-storage\native_scalar_harness.exe
if errorlevel 1 goto failed
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O1 /fsanitize=address /Zi tests\AgentStorage\native_scalar_harness.cpp /Fo:build\agent-storage\native_scalar_harness_asan.obj /Fe:build\agent-storage\native_scalar_harness_asan.exe /Fd:build\agent-storage\native_scalar_harness_asan.pdb
if errorlevel 1 goto failed
build\agent-storage\native_scalar_harness_asan.exe
:failed
set "test_result=%errorlevel%"
popd
exit /b %test_result%
