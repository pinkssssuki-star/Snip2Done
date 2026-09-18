@echo off
title RemeberEr - native WPF benchmark
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" -Bench -Primary
if errorlevel 1 pause
