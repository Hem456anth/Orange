@echo off
rem Orange command line: "orange convert photo.png --to webp". See docs\CLI.md.
"%~dp0..\Orange.exe" --cli %*
exit /b %ERRORLEVEL%
