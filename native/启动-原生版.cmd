@echo off
title RemeberEr - native WPF (no Chromium)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1"
if errorlevel 1 pause
