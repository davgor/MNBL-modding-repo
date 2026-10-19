@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0..\.."
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O2 tests\AgentStorage\native_agent_transaction_review.cpp /Fo:build\agent-storage\native_agent_transaction_review.obj /Fe:build\agent-storage\native_agent_transaction_review.exe
if errorlevel 1 goto done
build\agent-storage\native_agent_transaction_review.exe
if errorlevel 1 goto done
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O1 /fsanitize=address /Zi tests\AgentStorage\native_agent_transaction_review.cpp /Fo:build\agent-storage\native_agent_transaction_review_asan.obj /Fe:build\agent-storage\native_agent_transaction_review_asan.exe /Fd:build\agent-storage\native_agent_transaction_review_asan.pdb
if errorlevel 1 goto done
build\agent-storage\native_agent_transaction_review_asan.exe
:done
set "test_result=%errorlevel%"
popd
exit /b %test_result%
