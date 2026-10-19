@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0..\.."
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O2 tests\AgentStorage\empty_native_snapshot_tests.cpp /Fo:build\agent-storage\empty_native_snapshot_tests.obj /Fe:build\agent-storage\empty_native_snapshot_tests.exe
if errorlevel 1 goto done
build\agent-storage\empty_native_snapshot_tests.exe
if errorlevel 1 goto done
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O1 /fsanitize=address /Zi tests\AgentStorage\empty_native_snapshot_tests.cpp /Fo:build\agent-storage\empty_native_snapshot_asan.obj /Fe:build\agent-storage\empty_native_snapshot_asan.exe /Fd:build\agent-storage\empty_native_snapshot_asan.pdb
if errorlevel 1 goto done
build\agent-storage\empty_native_snapshot_asan.exe
:done
set "test_result=%errorlevel%"
popd
exit /b %test_result%
