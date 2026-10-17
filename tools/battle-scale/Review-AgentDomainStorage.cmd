@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0..\.."
build\agent-storage\agent_domain_storage_tests.exe
if errorlevel 1 goto failed
build\agent-storage\agent_domain_storage_asan.exe
:failed
set "test_result=%errorlevel%"
popd
exit /b %test_result%
