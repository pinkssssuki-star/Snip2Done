@echo off
title RemeberEr - WPF + WebView2 (layered / transparent)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" -Mode layered
if errorlevel 1 pause
