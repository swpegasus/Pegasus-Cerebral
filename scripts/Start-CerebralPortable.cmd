@echo off
start "" /D "%~dp0Applications\Desktop" "%~dp0Applications\Desktop\Cerebral.Integrator.exe" --portable-package
exit /b %errorlevel%
