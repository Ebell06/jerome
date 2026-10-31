@echo off
cd /d "%~dp0"
echo Opening Jerome Hologram in app mode...
start chrome --app="file:///%cd:\=/%/hologram-display.html" --window-size=720,720
