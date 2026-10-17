@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0..\.."
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O2 tests\AgentStorage\unpublished_element_probe_tests.cpp /Fo:build\agent-storage\unpublished_element_probe_tests.obj /Fe:build\agent-storage\unpublished_element_probe_tests.exe
if errorlevel 1 goto failed
build\agent-storage\unpublished_element_probe_tests.exe
if errorlevel 1 goto failed
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /Zi /fsanitize=address /Od tests\AgentStorage\unpublished_element_probe_tests.cpp /Fo:build\agent-storage\unpublished_element_probe_asan.obj /Fd:build\agent-storage\unpublished_element_probe_asan.pdb /Fe:build\agent-storage\unpublished_element_probe_asan.exe
if errorlevel 1 goto failed
build\agent-storage\unpublished_element_probe_asan.exe
if errorlevel 1 goto failed
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O2 /LD src\MNBL.AgentGrowthProbe\NativeElementProbe.cpp bcrypt.lib /Fo:build\agent-storage\NativeElementProbe.obj /Fe:build\agent-storage\MNBL.NativeElementProbe.dll
:failed
set "test_result=%errorlevel%"
popd
exit /b %test_result%
