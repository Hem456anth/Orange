@echo off
rem Orange command line: "orange convert photo.png --to webp". See docs\CLI.md.
"%~dp0..\orange-cli.exe" %*
exit /b %ERRORLEVEL%
