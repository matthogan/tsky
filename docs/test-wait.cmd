@echo off
echo Waiting 30 seconds...
rem timeout.exe requires interactive stdin and can fail under redirected agent execution.
ping 127.0.0.1 -n 31 >nul
echo Finished waiting.
