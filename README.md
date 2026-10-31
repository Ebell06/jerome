# Jerome

**A voice-controlled desk assistant for Windows, built in C# / .NET.**

Say "Hey Jerome" and your workspace boots itself: music starts, dashboards open, your editor comes into focus, and Jerome greets you out loud. From there you can control music, add tasks, check the weather, and launch tools entirely by voice.

Jerome started as a double-clap trigger script in Python and was rewritten in C# with wake-word recognition, a local dashboard server, and an explicit safety model for every action it can take.

---

## Features

- **Wake word:** offline speech recognition (`System.Speech`) listens for "Hey Jerome" with a tunable confidence threshold to limit false triggers.
- **Workspace boot sequence:** opens Chrome (Claude tab, HUD dashboards, hologram view), plays a Spotify track, speaks a greeting, and focuses Cursor.
- **Natural voice:** text-to-speech through the ElevenLabs API, played back with NAudio.
- **Music control:** "play `<song>`" searches the Spotify Web API and plays the top result; "next song" skips tracks.
- **Voice-to-dashboard tasks:** "add task `<task>`" posts to a small local server, and the task appears in the HUD's Tasks tab.
- **Quick info:** weather (via wttr.in, no API key), current time, and a status check.
- **HUD dashboards:** browser-based live wallpapers, including a knowledge-graph HUD, a trade-desk view, and a square hologram display for a Pepper's Ghost build.
- **Toast notifications:** Windows popups on wake, sleep, exit, and server errors, so you get feedback even when Jerome runs hidden.
- **Sleep vs. power off:** "shut down" closes your apps but keeps Jerome listening; "jerome power off" exits the program.
- **Paper-trading companion (optional):** "start trading bot" launches a separate paper-money-only bot and shows its status on the Trade Desk.

## Voice commands

| Say | What happens |
|---|---|
| "Hey Jerome" | Runs the full wake-up sequence |
| "play `<song name>`" | Searches Spotify and plays the top result |
| "next song" / "skip song" | Skips to the next track |
| "add task `<task>`" | Adds the task to the HUD's Tasks tab |
| "what's the weather" | Speaks the current weather |
| "what time is it" | Speaks the current time |
| "jerome are you there" / "jerome status" | Confirms Jerome is online |
| "start trading bot" / "show trading bot" | Starts the paper-trading bot / opens its dashboard |
| "shut down" / "goodnight jerome" | Sleep mode: closes Spotify, Cursor, and Jerome's windows, keeps listening |
| "jerome power off" | Fully exits Jerome |

## Safety by design

Jerome can only do what is written down. Three files define its boundaries:

- **`TOOLS.md`** lists every action Jerome can take, each labeled `SAFE`, `RISKY`, or `DENY`. Code that does something not in this file is treated as a bug.
- **`SOUL.md`** describes what Jerome is and is not meant to do.
- **`KILLSWITCH.md`** documents two independent ways to stop Jerome: a `KILL` file checked every 2 seconds (works even if speech recognition freezes) and `Stop Jerome.bat`.

Jerome never deletes files outside its own folder, sends messages, makes purchases, uses the webcam, changes Windows settings, or accepts remote commands. Windows are closed by exact name or title match, never with a blanket "close everything." An optional `setup-firewall.ps1` restricts the executable to HTTPS and DNS traffic at the firewall level.

## Tech stack

| Area | Tools |
|---|---|
| Core app | C#, .NET (Windows), `System.Speech`, Windows Forms (`NotifyIcon`) |
| Audio | NAudio, ElevenLabs text-to-speech API |
| Integrations | Spotify Web API, wttr.in, local HTTP server |
| Dashboards | HTML, CSS, JavaScript |
| Trading bot | Python (separate project, paper money only) |

## Getting started

**Requirements:** Windows 10/11, the .NET SDK, a microphone, Spotify, and Chrome.

1. Clone the repo and open the folder:
   ```
   git clone https://github.com/Ebell06/jerome.git
   cd jerome
   ```
2. Copy `.env.example` to `.env` and add your keys:
   ```
   ELEVENLABS_API_KEY=
   ELEVENLABS_VOICE_ID=
   SPOTIFY_CLIENT_ID=
   SPOTIFY_CLIENT_SECRET=
   ```
   Optional overrides: `SONG_URI`, `CHROME_URL`, `WELCOME_PHRASE`, `TRADEBOT_PATH`.
3. Build and run:
   ```
   dotnet build
   dotnet run
   ```
4. Say "Hey Jerome."

Song search turns off automatically if the Spotify keys are missing; everything else still works.

### Run it in the background

- **`Start Jerome.bat`:** one-click start with a visible console.
- **`Jerome Silent Start.vbs`:** runs with no window. Put a shortcut to it in your Startup folder (`Win+R` → `shell:startup`) to launch Jerome at every login.
- **`Stop Jerome.bat`:** force-stops Jerome.

## Project structure

```
jerome/
├── Program.cs                # wake word, voice commands, workflow, local server
├── JeromeSharp.csproj        # project file
├── .env.example              # template for API keys and settings
├── SOUL.md                   # what Jerome is and isn't
├── TOOLS.md                  # every action Jerome can take, risk-classified
├── KILLSWITCH.md             # two independent ways to stop Jerome
├── setup-firewall.ps1        # optional network restriction (run as admin)
├── hud-dashboard.html        # knowledge-graph HUD + Tasks tab
├── trading-dashboard.html    # trade-desk HUD
├── hologram-display.html     # square HUD for a Pepper's Ghost display
├── Start Jerome.bat
├── Stop Jerome.bat
└── Jerome Silent Start.vbs
```

## Roadmap

- [ ] Persistent log file for runs in hidden mode
- [ ] Watchdog to restart Jerome if the speech engine hangs
- [ ] Reminders and timers by voice
- [ ] Live market data in the HUD ticker

## Disclaimer

The trading bot uses paper money only and is not financial advice. Never commit your `.env` file; it is listed in `.gitignore`.

## Author

**Eric Bell** · [LinkedIn](https://www.linkedin.com/in/eric-bell-44743b1a5/)
