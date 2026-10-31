' Runs Jerome silently in the background — no visible window.
' To use: create a shortcut to this file and put that shortcut in your
' Startup folder (Win+R, type shell:startup, Enter, paste shortcut there).
' Jerome will then start automatically every time you log into Windows.

Set WshShell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)

WshShell.Run "cmd /c cd /d """ & scriptDir & """ && dotnet run", 0, False
