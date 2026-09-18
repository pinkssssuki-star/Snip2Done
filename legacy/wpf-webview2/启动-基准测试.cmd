@echo off
title RemeberEr - benchmark (memory / startup / capture latency)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" -Mode layered -Bench -Primary
if errorlevel 1 pause
