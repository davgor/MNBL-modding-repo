@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0..\.."
if not exist build\agent-storage mkdir build\agent-storage
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O2 tests\AgentStorage\agent_domain_storage_tests.cpp /Fo:build\agent-storage\agent_domain_storage_tests.obj /Fe:build\agent-storage\agent_domain_storage_tests.exe
if errorlevel 1 goto failed
build\agent-storage\agent_domain_storage_tests.exe
:failed
set "test_result=%errorlevel%"
popd
exit /b %test_result%
