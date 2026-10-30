@echo off
REM Run build\publish.ps1 with an execution-policy bypass so it works on machines
REM whose PowerShell policy is Restricted/undetermined without changing that policy.
REM CI already invokes the script the same way (-ExecutionPolicy Bypass -File); this
REM wrapper just makes a directly-visible run work too. All arguments are forwarded
REM to the script (e.g. -SelfContained, -Admin).
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
exit /b %ERRORLEVEL%
