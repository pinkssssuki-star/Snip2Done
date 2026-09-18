@echo off
title RemeberEr - pure native WPF (no WebView2)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" -Mode native
if errorlevel 1 pause
