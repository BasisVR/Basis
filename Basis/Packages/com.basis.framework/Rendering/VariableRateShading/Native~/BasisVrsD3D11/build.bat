@echo off
rem Builds BasisVrsD3D11.dll into ..\..\Plugins\x86_64 with the newest installed MSVC.
setlocal
set "VCVARS="
for /d %%v in ("%ProgramFiles%\Microsoft Visual Studio\*") do for /d %%e in ("%%v\*") do if exist "%%e\VC\Auxiliary\Build\vcvars64.bat" set "VCVARS=%%e\VC\Auxiliary\Build\vcvars64.bat"
if not defined VCVARS (echo MSVC not found & exit /b 1)
call "%VCVARS%" >nul || exit /b 1
cd /d "%~dp0"
set "OBJ=%TEMP%\BasisVrsD3D11"
if not exist "%OBJ%" mkdir "%OBJ%"
cl /nologo /O2 /MT /W4 /GS /LD /Fo"%OBJ%\\" BasisVrsD3D11.cpp /link /DLL /OUT:..\..\Plugins\x86_64\BasisVrsD3D11.dll /IMPLIB:"%OBJ%\BasisVrsD3D11.lib" /PDB:"%OBJ%\BasisVrsD3D11.pdb" || exit /b 1
