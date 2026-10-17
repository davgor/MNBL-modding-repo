@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0..\.."
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O2 tests\AgentStorage\native_agent_transaction_tests.cpp /Fo:build\agent-storage\native_agent_transaction_tests.obj /Fe:build\agent-storage\native_agent_transaction_tests.exe
if errorlevel 1 goto failed
build\agent-storage\native_agent_transaction_tests.exe
if errorlevel 1 goto failed
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O1 /fsanitize=address /Zi tests\AgentStorage\native_agent_transaction_tests.cpp /Fo:build\agent-storage\native_agent_transaction_asan.obj /Fd:build\agent-storage\native_agent_transaction_asan.pdb /Fe:build\agent-storage\native_agent_transaction_asan.exe
if errorlevel 1 goto failed
build\agent-storage\native_agent_transaction_asan.exe
:failed
set "test_result=%errorlevel%"
popd
exit /b %test_result%
