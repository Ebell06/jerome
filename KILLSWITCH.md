# KILLSWITCH.md

Voice recognition can fail, hang, or mishear. A kill switch that depends on
Jerome correctly hearing you defeats the point of a kill switch. Jerome has
two independent ways to stop it:

## 1. File-based kill switch (works even if voice recognition is frozen)
Create an empty file named exactly `KILL` in the same folder as `Program.cs`.
Jerome checks for this file every 2 seconds, completely independent of the
speech recognition engine. The moment it exists, Jerome exits immediately —
no goodbye line, no cleanup, just stops.

Fastest way to trigger it, from a terminal in that folder:
```
echo. > KILL
```
Or just create an empty file named `KILL` in File Explorer.

Jerome deletes the file on its way out, so it's ready to use again next time
without you having to remember to delete it yourself.

## 2. Stop Jerome.bat
Force-kills the process by name (`taskkill /F /IM JeromeSharp.exe`). Use this
if the file-based switch somehow doesn't work either (e.g. the process is
stuck in a way that isn't even checking for the file anymore).

## Why two mechanisms
Defense in depth, same principle as everything else in this project: no
single failure point should be the only thing standing between you and
Jerome actually stopping.
