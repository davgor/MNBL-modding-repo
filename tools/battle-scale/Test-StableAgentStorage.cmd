@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0..\.."
if not exist build\agent-storage mkdir build\agent-storage
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O2 tests\AgentStorage\stable_storage_tests.cpp /Fo:build\agent-storage\stable_storage_tests.obj /Fe:build\agent-storage\stable_storage_tests.exe
if errorlevel 1 exit /b %errorlevel%
build\agent-storage\stable_storage_tests.exe
set "test_result=%errorlevel%"
popd
exit /b %test_result%
