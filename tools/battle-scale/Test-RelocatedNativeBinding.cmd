@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
pushd "%~dp0..\.."
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O2 tests\AgentStorage\relocated_native_binding_tests.cpp bcrypt.lib /Fo:build\agent-storage\relocated_native_binding_tests.obj /Fe:build\agent-storage\relocated_native_binding_tests.exe
if errorlevel 1 goto failed
build\agent-storage\relocated_native_binding_tests.exe
if errorlevel 1 goto failed
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /Zi /fsanitize=address /Od tests\AgentStorage\relocated_native_binding_tests.cpp bcrypt.lib /Fo:build\agent-storage\relocated_native_binding_asan.obj /Fd:build\agent-storage\relocated_native_binding_asan.pdb /Fe:build\agent-storage\relocated_native_binding_asan.exe
if errorlevel 1 goto failed
build\agent-storage\relocated_native_binding_asan.exe
if errorlevel 1 goto failed
cl /nologo /std:c++20 /EHsc /W4 /WX /MD /O2 /LD src\MNBL.AgentGrowthProbe\RelocatedNativeElementProbe.cpp bcrypt.lib /Fo:build\agent-storage\RelocatedNativeElementProbe.obj /Fe:build\agent-storage\MNBL.RelocatedNativeElementProbe.dll
:failed
set "test_result=%errorlevel%"
popd
exit /b %test_result%
