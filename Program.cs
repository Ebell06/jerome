// JeromeSharp — "Hey Jerome" wake-word desk workflow trigger
//
// Setup:
//   1. dotnet add package NAudio
//   2. dotnet add package System.Speech
//   3. Target framework in JeromeSharp.csproj must end in "-windows" (e.g. net10.0-windows)
//      since System.Speech is Windows-only.
//   4. Set these environment variables (or load from a .env-style file — see LoadEnvFile below):
//        ELEVENLABS_API_KEY
//        ELEVENLABS_VOICE_ID
//   5. dotnet run
//
// What it does when it hears "Hey Jerome":
//   - Opens Spotify to a track URI
//   - Opens Chrome to a URL (e.g. Claude)
//   - Calls ElevenLabs TTS and plays the welcome line
//   - Brings Cursor to the foreground (Windows only, via P/Invoke)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using NAudio.Wave;

class Jerome
{
    // ---- Configuration (defaults below; overridden by .env if those keys are set) ----
    static string SongUri = "spotify:track:YOUR_TRACK_ID";
    static string ChromeUrl = "https://claude.ai/new";
    const int DashboardPort = 8000;
    static readonly string[] DashboardPaths = { "hud-dashboard.html", "trading-dashboard.html" };
    const string HologramPath = "hologram-display.html"; // no longer auto-opened on wake, still usable via Open Hologram.bat
    static string WelcomePhrase = "Welcome back. Jerome here. How can I help you, boss?";

    // God's Eye View (github.com/bilawalsidhu/gods-eye-view) — a separate Node/Vite
    // project, cloned and run independently of Jerome's own C# server.
    static string GevPath = @"C:\gods-eye-view"; // override with GEV_PATH in .env if cloned elsewhere
    const int GevPort = 4173;

    // Paper trading bot (the separate Python project from the "Paper trading bot" chat).
    // FAKE MONEY ONLY — that project has no code path that can place a real order.
    static string TradebotPath = @"C:\tradebot"; // override with TRADEBOT_PATH in .env
    static string PythonCmd = "python";          // override with PYTHON_CMD in .env (e.g. py)
    const int BotPort = 8001;                    // 8000 is Jerome's own server

    // For voice song search ("play <song>") — get these free from
    // https://developer.spotify.com/dashboard (create an app, no approval needed
    // for this kind of use). Leave blank to disable song search.
    static string SpotifyClientId = "";
    static string SpotifyClientSecret = "";
    static string? _spotifyAccessToken;
    static DateTime _spotifyTokenExpiry = DateTime.MinValue;

    // Phrases that count as the wake word — add variants here if it keeps mishearing you.
    static readonly string[] WakePhrases = { "hey jerome", "okay jerome", "hi jerome", "yo jerome", "hey germ" };
    // Phrases that skip to the next Spotify track instead of running the full wake-up sequence.
    static readonly string[] SkipPhrases = { "next song", "skip song", "hey jerome next song", "hey jerome skip song" };
    // Phrases that close everything Jerome opened but keep Jerome itself listening (sleep mode).
    static readonly string[] ShutdownPhrases = { "shut down", "shutdown jerome", "hey jerome shut down", "jerome shut down", "goodnight jerome" };
    // Phrases that fully quit the Jerome program itself (rarely needed — use this or Stop Jerome.bat).
    static readonly string[] ExitPhrases = { "jerome power off", "quit jerome completely", "exit jerome program" };
    static readonly string[] WeatherPhrases = { "what's the weather", "hey jerome what's the weather", "how's the weather" };
    static readonly string[] TimePhrases = { "what time is it", "hey jerome what time is it" };
    static readonly string[] StatusPhrases = { "hey jerome are you there", "jerome you there", "jerome status" };
    // Two-step task capture: say one of these, Jerome asks "What's the task?", then you say the task.
    static readonly string[] TaskPromptPhrases = { "add task", "new task", "add a task", "hey jerome add task", "hey jerome new task" };
    // Removes the most recently added task (handy when the mic mishears you).
    static readonly string[] UndoTaskPhrases = { "undo task", "delete last task", "remove last task", "hey jerome undo task", "hey jerome delete last task" };
    // Re-opens all the screens on demand (plain "Hey Jerome" no longer does once Jerome is already awake).
    static readonly string[] ShowScreensPhrases = { "show screens", "open everything", "hey jerome show screens", "hey jerome open everything" };
    static readonly string[] StartBotPhrases = { "start trading bot", "start the trading bot", "run trading bot", "hey jerome start trading bot" };
    static readonly string[] StopBotPhrases = { "stop trading bot", "stop the trading bot", "hey jerome stop trading bot" };
    static readonly string[] BotStatusPhrases = { "trading status", "bot status", "how is the bot doing", "how's the bot doing", "hey jerome trading status" };
    static readonly string[] ShowBotPhrases = { "show trading bot", "open trading bot", "show bot dashboard" };
    const string WeatherCity = "San Antonio"; // change to your city
    const double MinConfidence = 0.4; // 0.0–1.0, lower = triggers more easily (and more false positives)

    static readonly HttpClient Http = new();
    static bool _busy = false;
    static bool _awake = false;            // true after the workflow has opened the screens, until sleep
    static bool _capturingTask = false;    // true while waiting for the spoken task text
    static Grammar? _dictation;            // pure dictation grammar, only enabled during task capture
    static SpeechRecognitionEngine? _recognizer;
    static TaskCompletionSource _exitSignal = new();
    static NotifyIcon? _notifyIcon;

    static async Task Main()
    {
        LoadEnvFile(".env"); // simple .env loader, see below

        // Apply .env overrides on top of the defaults above, if those keys are set
        SongUri = Environment.GetEnvironmentVariable("SONG_URI") ?? SongUri;
        ChromeUrl = Environment.GetEnvironmentVariable("CHROME_URL") ?? ChromeUrl;
        WelcomePhrase = Environment.GetEnvironmentVariable("WELCOME_PHRASE") ?? WelcomePhrase;
        SpotifyClientId = Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_ID") ?? "";
        SpotifyClientSecret = Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_SECRET") ?? "";
        GevPath = Environment.GetEnvironmentVariable("GEV_PATH") ?? GevPath;
        TradebotPath = Environment.GetEnvironmentVariable("TRADEBOT_PATH") ?? TradebotPath;
        PythonCmd = Environment.GetEnvironmentVariable("PYTHON_CMD") ?? PythonCmd;

        Console.WriteLine("Jerome listening for \"Hey Jerome\"... Ctrl+C to quit.");
        if (string.IsNullOrEmpty(SpotifyClientId) || string.IsNullOrEmpty(SpotifyClientSecret))
            Console.WriteLine("(Song search disabled — add SPOTIFY_CLIENT_ID / SPOTIFY_CLIENT_SECRET to .env to enable \"play <song>\".)");

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Text = "Jerome",
            Visible = true
        };
        Notify("Jerome is online", "Listening for \"Hey Jerome\"...");

        _recognizer = new SpeechRecognitionEngine();

        // Fixed commands (wake word + skip + shutdown + weather + time + status)
        var choices = new Choices();
        choices.Add(WakePhrases);
        choices.Add(SkipPhrases);
        choices.Add(ShutdownPhrases);
        choices.Add(ExitPhrases);
        choices.Add(WeatherPhrases);
        choices.Add(TimePhrases);
        choices.Add(StatusPhrases);
        choices.Add(TaskPromptPhrases);
        choices.Add(UndoTaskPhrases);
        choices.Add(ShowScreensPhrases);
        choices.Add(StartBotPhrases);
        choices.Add(StopBotPhrases);
        choices.Add(BotStatusPhrases);
        choices.Add(ShowBotPhrases);
        _recognizer.LoadGrammar(new Grammar(new GrammarBuilder(choices)));

        // Free-form song search: "play <anything>" or "hey jerome play <anything>"
        var playBuilder = new GrammarBuilder();
        playBuilder.Append(new Choices("play", "hey jerome play"));
        playBuilder.AppendDictation();
        _recognizer.LoadGrammar(new Grammar(playBuilder));

        // Free-form task capture: "add task <anything>" or "hey jerome add task <anything>"
        var taskBuilder = new GrammarBuilder();
        taskBuilder.Append(new Choices("add task", "hey jerome add task"));
        taskBuilder.AppendDictation();
        _recognizer.LoadGrammar(new Grammar(taskBuilder));

        // Pure dictation, switched on only for the few seconds after "What's the task?"
        _dictation = new DictationGrammar { Enabled = false };
        _recognizer.LoadGrammar(_dictation);

        _recognizer.SetInputToDefaultAudioDevice();
        _recognizer.SpeechRecognized += OnSpeechRecognized;
        _recognizer.RecognizeAsync(RecognizeMode.Multiple);

        // File-based kill switch (see KILLSWITCH.md) — works even if speech
        // recognition itself is hung or misbehaving, since it never touches it.
        _ = KillSwitchWatcherAsync();

        // Keep the app alive until Ctrl+C, "shut down"/"power off", or the kill switch
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _exitSignal.TrySetResult(); };
        await _exitSignal.Task;

        _recognizer.RecognizeAsyncStop();
        _recognizer.Dispose();
        Console.WriteLine("Jerome has shut down.");
    }

    // ---- Speech recognition routing ----
    static void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        string heard = e.Result.Text.ToLowerInvariant();

        // Task capture mode: the next dictation result is the task text.
        if (_capturingTask)
        {
            if (e.Result.Grammar != _dictation) return;
            if (e.Result.Confidence < 0.2) return;
            _capturingTask = false;
            if (_dictation != null) _dictation.Enabled = false;
            string spoken = e.Result.Text.Trim();
            Console.WriteLine($"[SAFE] Heard task: \"{spoken}\" (confidence {e.Result.Confidence:P0})");
            if (heard is "cancel" or "never mind" or "nevermind" or "cancel task")
            {
                _ = SpeakWelcomeAsync("Cancelled.");
                return;
            }
            _ = AddTaskAsync(spoken);
            return;
        }

        // "play <song>" — free-form dictation results score lower even when correct,
        // so this uses a looser confidence bar than the fixed commands below.
        if (heard.StartsWith("hey jerome play ") || heard.StartsWith("play "))
        {
            if (e.Result.Confidence < 0.25) return;
            string prefix = heard.StartsWith("hey jerome play ") ? "hey jerome play " : "play ";
            string query = e.Result.Text.Substring(prefix.Length).Trim();
            Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" (confidence {e.Result.Confidence:P0})");
            _ = SearchAndPlaySongAsync(query);
            return;
        }

        // "add task <text>" — free-form dictation, same looser confidence bar as "play"
        if (heard.StartsWith("hey jerome add task ") || heard.StartsWith("add task "))
        {
            if (e.Result.Confidence < 0.25) return;
            string prefix = heard.StartsWith("hey jerome add task ") ? "hey jerome add task " : "add task ";
            string taskText = e.Result.Text.Substring(prefix.Length).Trim();
            Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" (confidence {e.Result.Confidence:P0})");
            _ = AddTaskAsync(taskText);
            return;
        }

        if (e.Result.Confidence < MinConfidence) return;

        if (ExitPhrases.Contains(heard))
        {
            Console.WriteLine($"[RISKY] Heard: \"{e.Result.Text}\" (confidence {e.Result.Confidence:P0}) — exiting program");
            _ = ExitJeromeAsync();
            return;
        }

        if (ShutdownPhrases.Contains(heard))
        {
            Console.WriteLine($"[RISKY] Heard: \"{e.Result.Text}\" (confidence {e.Result.Confidence:P0}) — going to sleep");
            _ = SleepAsync();
            return;
        }

        if (SkipPhrases.Contains(heard))
        {
            Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" (confidence {e.Result.Confidence:P0}) — skipping track");
            SkipToNextSong();
            return;
        }

        if (WeatherPhrases.Contains(heard))
        {
            Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" (confidence {e.Result.Confidence:P0}) — checking weather");
            _ = SpeakWeatherAsync();
            return;
        }

        if (TimePhrases.Contains(heard))
        {
            Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" (confidence {e.Result.Confidence:P0}) — telling the time");
            _ = SpeakWelcomeAsync($"It's {DateTime.Now:h:mm tt}.");
            return;
        }

        if (TaskPromptPhrases.Contains(heard))
        {
            Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" (confidence {e.Result.Confidence:P0}) — asking for task");
            _ = PromptForTaskAsync();
            return;
        }

        if (UndoTaskPhrases.Contains(heard))
        {
            Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" (confidence {e.Result.Confidence:P0}) — removing last task");
            _ = UndoLastTaskAsync();
            return;
        }

        if (ShowScreensPhrases.Contains(heard))
        {
            if (_busy) return;
            Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" (confidence {e.Result.Confidence:P0}) — opening screens");
            _busy = true;
            _ = RunWorkflowAsync();
            return;
        }

        if (StartBotPhrases.Contains(heard)) { Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" — starting paper trading bot"); _ = StartTradingBotAsync(); return; }
        if (StopBotPhrases.Contains(heard)) { Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" — stopping paper trading bot"); _ = StopTradingBotAsync(true); return; }
        if (BotStatusPhrases.Contains(heard)) { Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" — bot status"); _ = SpeakBotStatusAsync(); return; }
        if (ShowBotPhrases.Contains(heard)) { Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" — showing bot dashboard"); OpenChromeAppMode($"http://127.0.0.1:{BotPort}", 1100, 800); return; }

        if (StatusPhrases.Contains(heard))
        {
            Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" (confidence {e.Result.Confidence:P0}) — status check");
            _ = SpeakWelcomeAsync("Yes, I'm here. All systems online.");
            return;
        }

        if (_busy) return;
        // Already awake: don't re-open and re-tile everything, just answer.
        if (_awake)
        {
            Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" (confidence {e.Result.Confidence:P0}) — already awake");
            _ = SpeakWelcomeAsync("Yes boss?");
            return;
        }
        Console.WriteLine($"[SAFE] Heard: \"{e.Result.Text}\" (confidence {e.Result.Confidence:P0})");
        _busy = true;
        _ = RunWorkflowAsync(); // fire and forget
    }

    // ---- Kill switch: watches for a "KILL" file, completely independent of speech
    // recognition, so it works even if that engine is hung or misbehaving. See KILLSWITCH.md.
    static async Task KillSwitchWatcherAsync()
    {
        string killFile = Path.Combine(Directory.GetCurrentDirectory(), "KILL");
        while (true)
        {
            try
            {
                if (File.Exists(killFile))
                {
                    Console.WriteLine("[RISKY] Kill switch triggered — exiting immediately, no cleanup.");
                    try { File.Delete(killFile); } catch { } // reset for next time
                    Environment.Exit(0);
                }
            }
            catch (Exception ex) { Console.WriteLine($"Kill switch watcher error: {ex.Message}"); }
            await Task.Delay(2000);
        }
    }

    // ---- Sleep: say goodbye, close what Jerome opened, but keep Jerome itself listening ----
    static async Task SleepAsync()
    {
        _awake = false;
        _capturingTask = false;
        if (_dictation != null) _dictation.Enabled = false;
        try { await SpeakWelcomeAsync("Going to sleep. Say \"Hey Jerome\" when you need me, boss."); }
        catch (Exception ex) { Console.WriteLine($"Sleep line failed: {ex.Message}"); }

        CloseApp("Spotify");
        CloseApp("Cursor");

        // Close just the hub/hologram windows by their titles (set in each HTML file's
        // <title> tag), leaving any other Chrome windows/tabs you have open untouched.
        CloseWindowsByTitleContains("JEROME // OPTIMAL ENGINE");
        CloseWindowsByTitleContains("JEROME // TRADE DESK");
        CloseWindowsByTitleContains("JEROME // HOLOGRAM");
        CloseWindowsByTitleContains("Eye View"); // God's Eye View
        // Closes the exact Claude window Jerome opened (tracked during tiling) —
        // not a broad title match, so any other Claude window you have open elsewhere is safe.
        CloseSpecificWindow(ref _claudeWindowHandle);

        Console.WriteLine("Asleep. Still listening for \"Hey Jerome\"...");
        Notify("Jerome is asleep", "Say \"Hey Jerome\" to wake me back up.");
    }

    // ---- Fully quit the Jerome program itself (rare — usually you just want SleepAsync) ----
    static async Task ExitJeromeAsync()
    {
        try { await SpeakWelcomeAsync("Powering off completely. Goodbye, boss."); }
        catch (Exception ex) { Console.WriteLine($"Exit line failed: {ex.Message}"); }
        Notify("Jerome powered off", "The program has exited.");

        CloseApp("Spotify");
        CloseApp("Cursor");
        CloseWindowsByTitleContains("JEROME // OPTIMAL ENGINE");
        CloseWindowsByTitleContains("JEROME // TRADE DESK");
        CloseWindowsByTitleContains("JEROME // HOLOGRAM");
        CloseWindowsByTitleContains("Eye View"); // God's Eye View
        CloseSpecificWindow(ref _claudeWindowHandle);
        await StopTradingBotAsync(false); // full power off also stops the paper bot (sleep leaves it running)

        _exitSignal.TrySetResult();
    }

    static void CloseApp(string processName)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                p.CloseMainWindow();
                if (!p.WaitForExit(2000)) p.Kill();
            }
            catch (Exception ex) { Console.WriteLine($"Couldn't close {processName}: {ex.Message}"); }
        }
    }

    // ---- Spotify song search (Client Credentials — no user login needed) ----
    static async Task<string?> GetSpotifyTokenAsync()
    {
        if (_spotifyAccessToken != null && DateTime.UtcNow < _spotifyTokenExpiry) return _spotifyAccessToken;
        if (string.IsNullOrEmpty(SpotifyClientId) || string.IsNullOrEmpty(SpotifyClientSecret)) return null;

        try
        {
            var authBytes = Encoding.UTF8.GetBytes($"{SpotifyClientId}:{SpotifyClientSecret}");
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { { "grant_type", "client_credentials" } });

            var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            _spotifyAccessToken = doc.RootElement.GetProperty("access_token").GetString();
            int expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();
            _spotifyTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
            return _spotifyAccessToken;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Spotify auth failed: {ex.Message}");
            return null;
        }
    }

    // ---- Add a task by voice — writes to tasks.json, same file the dashboard reads ----
    static async Task AddTaskAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        AddTaskToFile(text);
        Console.WriteLine($"Task added: \"{text}\"");
        Notify("Task added", text);
        try { await SpeakWelcomeAsync($"Added: {text}."); }
        catch (Exception ex) { Console.WriteLine($"Task confirmation line failed: {ex.Message}"); }
    }

    // ---- Two-step capture: "add task" -> "What's the task?" -> listen for the task text ----
    static async Task PromptForTaskAsync()
    {
        if (_capturingTask) return;
        try { await SpeakWelcomeAsync("What's the task?"); }
        catch (Exception ex) { Console.WriteLine($"Prompt line failed: {ex.Message}"); }

        // Enable dictation only after Jerome finishes talking so he doesn't transcribe himself.
        _capturingTask = true;
        if (_dictation != null) _dictation.Enabled = true;
        Notify("Listening for your task", "Say the task, or say \"cancel\".");

        await Task.Delay(12000);
        if (_capturingTask)
        {
            _capturingTask = false;
            if (_dictation != null) _dictation.Enabled = false;
            Console.WriteLine("Task capture timed out.");
            try { await SpeakWelcomeAsync("Never mind, then."); } catch { }
        }
    }

    static async Task UndoLastTaskAsync()
    {
        var tasks = LoadTasksFromFile();
        if (tasks.Count == 0)
        {
            try { await SpeakWelcomeAsync("You have no tasks to remove."); } catch { }
            return;
        }
        var last = tasks[^1];
        string text = last.TryGetProperty("text", out var t) ? (t.GetString() ?? "") : "";
        tasks.RemoveAt(tasks.Count - 1);
        SaveTasksToFile(tasks);
        Console.WriteLine($"Task removed: \"{text}\"");
        Notify("Task removed", text);
        try { await SpeakWelcomeAsync($"Removed: {text}."); }
        catch (Exception ex) { Console.WriteLine($"Undo confirmation failed: {ex.Message}"); }
    }

    // ---- Weather — free, no API key (wttr.in) ----
    static async Task SpeakWeatherAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://wttr.in/{Uri.EscapeDataString(WeatherCity)}?format=%C+%t");
            req.Headers.UserAgent.ParseAdd("curl"); // wttr.in serves plain text only for curl-like user agents
            var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                await SpeakWelcomeAsync("Sorry, I couldn't reach the weather service.");
                return;
            }
            string result = (await resp.Content.ReadAsStringAsync()).Trim();
            Console.WriteLine($"Weather: {result}");
            await SpeakWelcomeAsync($"Right now in {WeatherCity}, it's {result}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Weather check failed: {ex.Message}");
            try { await SpeakWelcomeAsync("Sorry, I couldn't check the weather."); } catch { }
        }
    }

    static async Task SearchAndPlaySongAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        Console.WriteLine($"Searching Spotify for: \"{query}\"...");

        var token = await GetSpotifyTokenAsync();
        if (token == null)
        {
            Console.WriteLine("Spotify search isn't set up — add SPOTIFY_CLIENT_ID and SPOTIFY_CLIENT_SECRET to .env.");
            try { await SpeakWelcomeAsync("Spotify search isn't set up yet, boss."); } catch { }
            return;
        }

        try
        {
            var url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit=1";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"Spotify search failed: {resp.StatusCode}");
                try { await SpeakWelcomeAsync("Spotify search failed."); } catch { }
                return;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var items = doc.RootElement.GetProperty("tracks").GetProperty("items");
            if (items.GetArrayLength() == 0)
            {
                Console.WriteLine($"No results for \"{query}\".");
                try { await SpeakWelcomeAsync($"I couldn't find {query}."); } catch { }
                return;
            }

            var track = items[0];
            string trackId = track.GetProperty("id").GetString() ?? "";
            string trackName = track.GetProperty("name").GetString() ?? "";
            string artistName = track.GetProperty("artists")[0].GetProperty("name").GetString() ?? "";

            Console.WriteLine($"Playing \"{trackName}\" by {artistName}");
            try { await SpeakWelcomeAsync($"Playing {trackName} by {artistName}."); } catch { }
            await OpenSpotifyAsync($"spotify:track:{trackId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Spotify search error: {ex.Message}");
        }
    }

    // ---- Skip to next track (global media key, works with Spotify already open) ----
    static void SkipToNextSong()
    {
        try
        {
            keybd_event(VK_MEDIA_NEXT_TRACK, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MEDIA_NEXT_TRACK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch (Exception ex) { Console.WriteLine($"Skip failed: {ex.Message}"); }
    }
    const byte VK_MEDIA_NEXT_TRACK = 0xB0;

    // ---- The actual workflow, mirrors the Python script's sequence ----
    static async Task RunWorkflowAsync()
    {
        Console.WriteLine("Wake word detected! Running workflow...");
        Notify("Hey Jerome!", "Waking up — opening everything now.");
        try
        {
            await EnsureDashboardServerAsync();
            var dashboardUrls = DashboardPaths.Select(p => $"http://localhost:{DashboardPort}/{p}").ToArray();
            await OpenChromeAsync(new[] { ChromeUrl }.Concat(dashboardUrls).ToArray());

            await EnsureGevServerAsync();
            OpenChromeAppMode($"http://localhost:{GevPort}", 900, 700);

            _ = TileWindowsAsync(); // runs in the background, doesn't hold up the rest of the workflow

            await SpeakWelcomeAsync(WelcomePhrase);
            await OpenSpotifyAsync(SongUri);
            FocusCursorWindow();

            _awake = true;
            Console.WriteLine("Workflow complete. Listening for \"Hey Jerome\" again...");
        }
        catch (Exception ex)
        {
            Notify("Jerome hit an error", ex.Message);
            throw;
        }
        finally
        {
            _busy = false;
        }
    }

    static async Task OpenSpotifyAsync(string uri)
    {
        try
        {
            bool alreadyRunning = Process.GetProcessesByName("Spotify").Any();
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

            // Minimize the instant the window exists — checking fast and often keeps
            // the visible flash as short as physically possible, rather than waiting a
            // few seconds and minimizing only after it's already been sitting on screen.
            bool minimized = false;
            int maxChecks = alreadyRunning ? 20 : 60; // ~3s / ~9s worth of checks, cold starts take longer
            for (int i = 0; i < maxChecks && !minimized; i++)
            {
                await Task.Delay(150);
                var spotify = Process.GetProcessesByName("Spotify").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
                if (spotify != null)
                {
                    ShowWindow(spotify.MainWindowHandle, SW_MINIMIZE);
                    minimized = true;
                }
            }
            if (!minimized) Console.WriteLine("Couldn't find Spotify's window to minimize it.");

            // Global media keys work regardless of window state, so Play can be sent
            // after minimizing without needing to bring the window back up.
            await Task.Delay(alreadyRunning ? 800 : 2500); // let the track actually load before pressing play
            keybd_event(VK_SPACE, 0, 0, UIntPtr.Zero);
            keybd_event(VK_SPACE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch (Exception ex) { Console.WriteLine($"Spotify launch failed: {ex.Message}"); }
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    const byte VK_SPACE = 0x20;
    const uint KEYEVENTF_KEYUP = 0x0002;

    static async Task OpenChromeAsync(params string[] urls)
    {
        foreach (var url in urls)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "chrome",
                    Arguments = $"--new-window \"{url}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { Console.WriteLine($"Chrome launch failed for {url}: {ex.Message}"); }
            await Task.Delay(400); // small stagger so Chrome reliably opens separate windows, not merged tabs
        }
    }

    // Opens a URL with no browser chrome (no address bar/tabs) at a fixed size —
    // used for the hologram display, which is meant to look like a standalone app.
    static void OpenChromeAppMode(string url, int width, int height)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "chrome",
                Arguments = $"--app=\"{url}\" --window-size={width},{height}",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Console.WriteLine($"Hologram launch failed: {ex.Message}"); }
    }

    // ---- Local dashboard server: starts "dotnet serve" if it's not already running ----
    static async Task<bool> IsPortOpenAsync(int port, int timeoutMs = 800)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync("localhost", port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs));
            return completed == connectTask && client.Connected;
        }
        catch { return false; }
    }

    // ---- Built-in static file server for the dashboards ----
    // This used to shell out to the "dotnet-serve" tool, but Windows Application
    // Control policies were blocking that external executable when launched
    // programmatically (even though typing the same command yourself worked fine).
    // Serving the files from inside Jerome's own already-trusted process sidesteps
    // that entirely — no external program is ever launched.
    static HttpListener? _dashboardServer;
    static readonly Dictionary<string, string> MimeTypes = new()
    {
        {".html","text/html"}, {".htm","text/html"}, {".js","application/javascript"},
        {".css","text/css"}, {".json","application/json"}, {".png","image/png"},
        {".jpg","image/jpeg"}, {".jpeg","image/jpeg"}, {".svg","image/svg+xml"},
        {".ico","image/x-icon"}
    };

    static async Task EnsureDashboardServerAsync()
    {
        if (_dashboardServer != null || await IsPortOpenAsync(DashboardPort))
        {
            Console.WriteLine($"Dashboard server already running on port {DashboardPort}.");
            return;
        }

        Console.WriteLine($"Starting built-in dashboard server on port {DashboardPort}...");
        try
        {
            _dashboardServer = new HttpListener();
            _dashboardServer.Prefixes.Add($"http://localhost:{DashboardPort}/");
            _dashboardServer.Start();
            _ = Task.Run(DashboardServerLoop);
            Console.WriteLine("Dashboard server is ready.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Couldn't start dashboard server: {ex.Message}");
            Notify("Dashboard server failed", ex.Message);
            _dashboardServer = null;
        }
    }

    // ---- Paper trading bot (external Python project). It keeps running through sleep mode,
    // since it needs weeks of uptime to mean anything; only "stop trading bot" or a full
    // power off stops it. Dashboard + API live on BotPort. ----
    static Process? _botProcess;

    static async Task StartTradingBotAsync()
    {
        if (await IsPortOpenAsync(BotPort))
        {
            try { await SpeakWelcomeAsync("The trading bot is already running."); } catch { }
            return;
        }
        if (!File.Exists(Path.Combine(TradebotPath, "run.py")))
        {
            Console.WriteLine($"Trading bot not found at {TradebotPath}. Unzip it there or set TRADEBOT_PATH in .env.");
            try { await SpeakWelcomeAsync("I can't find the trading bot folder."); } catch { }
            return;
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {PythonCmd} run.py live --no-browser",
                WorkingDirectory = TradebotPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.Environment["DASHBOARD_PORT"] = BotPort.ToString();
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"[tradebot] {e.Data}"); };
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"[tradebot] {e.Data}"); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _botProcess = proc;

            for (int waited = 0; waited < 20000; waited += 400)
            {
                if (await IsPortOpenAsync(BotPort))
                {
                    Notify("Trading bot started", "Paper money only.");
                    try { await SpeakWelcomeAsync("Trading bot is running. Paper money only."); } catch { }
                    return;
                }
                if (proc.HasExited) break;
                await Task.Delay(400);
            }
            Console.WriteLine("Trading bot didn't come up. Check the [tradebot] lines above (missing python or pip install?).");
            try { await SpeakWelcomeAsync("The trading bot didn't start."); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Couldn't start trading bot: {ex.Message}");
            try { await SpeakWelcomeAsync("The trading bot didn't start."); } catch { }
        }
    }

    static async Task StopTradingBotAsync(bool speak)
    {
        try
        {
            if (_botProcess != null && !_botProcess.HasExited)
            {
                // /t kills the whole tree (cmd.exe -> python), /f forces it. State is saved by the bot every cycle.
                using var k = Process.Start(new ProcessStartInfo("taskkill", $"/f /t /pid {_botProcess.Id}") { CreateNoWindow = true, UseShellExecute = false });
                k?.WaitForExit(5000);
                _botProcess = null;
                if (speak) await SpeakWelcomeAsync("Trading bot stopped.");
            }
            else if (speak)
            {
                string msg = await IsPortOpenAsync(BotPort)
                    ? "That bot wasn't started by me. Close its window or terminal to stop it."
                    : "The trading bot isn't running.";
                await SpeakWelcomeAsync(msg);
            }
        }
        catch (Exception ex) { Console.WriteLine($"Stop trading bot failed: {ex.Message}"); }
    }

    // Reads the bot's own /api/state. Returns null if it isn't running.
    static async Task<JsonDocument?> GetBotStateAsync()
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(3000);
            var resp = await Http.GetAsync($"http://127.0.0.1:{BotPort}/api/state", cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    static async Task SpeakBotStatusAsync()
    {
        using var doc = await GetBotStateAsync();
        if (doc == null) { try { await SpeakWelcomeAsync("The trading bot isn't running."); } catch { } return; }
        double equity = 0, start = 0; int trades = 0; bool any = false;
        if (doc.RootElement.TryGetProperty("live", out var live))
            foreach (var m in live.EnumerateObject())
            {
                if (!m.Value.TryGetProperty("stats", out var st)) continue;
                any = true;
                if (st.TryGetProperty("equity", out var eq) && eq.TryGetDouble(out var ev)) equity += ev;
                if (st.TryGetProperty("trades", out var tr) && tr.TryGetInt32(out var tv)) trades += tv;
                // Starting cash defaults to $10,000 per market (config.py); use it if the snapshot reports it.
                start += st.TryGetProperty("start_cash", out var sc) && sc.TryGetDouble(out var sv) ? sv : 10000;
            }
        if (!any) { try { await SpeakWelcomeAsync("The bot is running but hasn't made any trades yet."); } catch { } return; }
        double pnl = equity - start;
        string dir = pnl >= 0 ? "up" : "down";
        string line = $"Paper accounts are {dir} {Math.Abs(pnl):N0} dollars, total {equity:N0}, across {trades} trades.";
        Console.WriteLine(line);
        Notify("Trading bot", line);
        try { await SpeakWelcomeAsync(line); } catch { }
    }

    // ---- God's Eye View (external Node/Vite project) — launches "npm run dev" in its
    // own folder. Unlike the dashboard server, this DOES spawn an external process
    // (npm/node), which is exactly the kind of thing Windows Application Control has
    // blocked before (see the dotnet-serve saga in this project's history). If this
    // fails, start it manually in its own terminal instead: cd into GevPath, npm run dev.
    static bool _gevStarting = false;

    static async Task EnsureGevServerAsync()
    {
        if (await IsPortOpenAsync(GevPort))
        {
            Console.WriteLine($"God's Eye View already running on port {GevPort}.");
            return;
        }
        if (_gevStarting) return; // avoid double-launching if triggered twice in quick succession
        _gevStarting = true;

        if (!Directory.Exists(GevPath))
        {
            Console.WriteLine($"God's Eye View folder not found at {GevPath}. Set GEV_PATH in .env if you cloned it elsewhere.");
            _gevStarting = false;
            return;
        }

        Console.WriteLine($"Starting God's Eye View (npm run dev) on port {GevPort}...");
        try
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c npm run dev",
                    WorkingDirectory = GevPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            // Drain output as it comes in — an unread buffer can silently stall a child
            // process, which is exactly what happened with the old dotnet-serve approach.
            proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"[gods-eye-view] {e.Data}"); };
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"[gods-eye-view] {e.Data}"); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            const int maxWaitMs = 20000; // npm/vite cold starts can take a while
            const int pollMs = 400;
            int waited = 0;
            while (waited < maxWaitMs)
            {
                if (await IsPortOpenAsync(GevPort))
                {
                    Console.WriteLine("God's Eye View is ready.");
                    _gevStarting = false;
                    return;
                }
                await Task.Delay(pollMs);
                waited += pollMs;
            }
            Console.WriteLine("God's Eye View didn't respond in time. If Windows blocked npm/node from launching " +
                "(Application Control has done this before with similar tools), start it manually instead: " +
                $"open a terminal in {GevPath} and run \"npm run dev\".");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Couldn't start God's Eye View: {ex.Message}");
            Notify("God's Eye View failed to start", ex.Message);
        }
        finally { _gevStarting = false; }
    }

    static async Task DashboardServerLoop()
    {
        while (_dashboardServer != null && _dashboardServer.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _dashboardServer.GetContextAsync(); }
            catch { break; } // listener was stopped
            _ = Task.Run(() => HandleDashboardRequest(ctx));
        }
    }

    static void HandleDashboardRequest(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            // ---- Tasks API: shared source of truth between voice ("add task") and the
            // hud-dashboard.html Tasks tab, backed by tasks.json in this same folder. ----
            if (path == "/api/tasks")
            {
                HandleTasksApi(ctx);
                return;
            }

            // ---- Paper bot proxy: lets the trade desk read the bot's numbers without CORS trouble ----
            if (path == "/api/bot")
            {
                using var botDoc = GetBotStateAsync().GetAwaiter().GetResult(); // handler runs on a worker thread
                ctx.Response.ContentType = "application/json";
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                byte[] body = Encoding.UTF8.GetBytes(botDoc == null ? "{\"running\":false}" : botDoc.RootElement.GetRawText());
                ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.Close();
                return;
            }

            if (path == "/") path = "/index.html";
            string filePath = Path.Combine(Directory.GetCurrentDirectory(), path.TrimStart('/'));

            if (File.Exists(filePath))
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                ctx.Response.ContentType = MimeTypes.TryGetValue(ext, out var mt) ? mt : "application/octet-stream";
                var bytes = File.ReadAllBytes(filePath);
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            else
            {
                ctx.Response.StatusCode = 404;
                var msg = Encoding.UTF8.GetBytes("404 Not Found");
                ctx.Response.OutputStream.Write(msg, 0, msg.Length);
            }
        }
        catch (Exception ex) { Console.WriteLine($"Dashboard server request error: {ex.Message}"); }
        finally { try { ctx.Response.OutputStream.Close(); } catch { } }
    }

    // ---- Shared tasks.json file, read/written by both voice commands and the dashboard ----
    static readonly string TasksFilePath = Path.Combine(Directory.GetCurrentDirectory(), "tasks.json");
    static readonly object TasksFileLock = new();

    static List<JsonElement> LoadTasksFromFile()
    {
        lock (TasksFileLock)
        {
            try
            {
                if (!File.Exists(TasksFilePath)) return new List<JsonElement>();
                var json = File.ReadAllText(TasksFilePath);
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
                return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            }
            catch { return new List<JsonElement>(); }
        }
    }

    static void SaveTasksToFile(List<JsonElement> tasks)
    {
        lock (TasksFileLock)
        {
            try { File.WriteAllText(TasksFilePath, JsonSerializer.Serialize(tasks)); }
            catch (Exception ex) { Console.WriteLine($"Couldn't save tasks.json: {ex.Message}"); }
        }
    }

    static void AddTaskToFile(string text)
    {
        var tasks = LoadTasksFromFile();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { text, done = false }));
        tasks.Add(doc.RootElement.Clone());
        SaveTasksToFile(tasks);
    }

    static void HandleTasksApi(HttpListenerContext ctx)
    {
        ctx.Response.ContentType = "application/json";
        // CORS: the dashboard is served from the same origin (localhost:8000), but allow
        // cross-origin fetches too in case someone previews the HTML from a different port.
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, OPTIONS");
        ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        if (ctx.Request.HttpMethod == "OPTIONS") { ctx.Response.StatusCode = 204; return; }

        if (ctx.Request.HttpMethod == "GET")
        {
            var tasks = LoadTasksFromFile();
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tasks));
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            return;
        }

        if (ctx.Request.HttpMethod == "POST")
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(body);
            string text = doc.RootElement.GetProperty("text").GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(text)) AddTaskToFile(text);

            var tasks = LoadTasksFromFile();
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tasks));
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            return;
        }

        if (ctx.Request.HttpMethod == "PUT")
        {
            // Dashboard pushes its full updated list back (after a toggle/delete),
            // keeping tasks.json as the single source of truth.
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "[]" : body);
            var tasks = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            SaveTasksToFile(tasks);

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tasks));
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            return;
        }

        ctx.Response.StatusCode = 405;
    }

    static async Task SpeakWelcomeAsync(string text)
    {
        string apiKey = Environment.GetEnvironmentVariable("ELEVENLABS_API_KEY") ?? "";
        string voiceId = Environment.GetEnvironmentVariable("ELEVENLABS_VOICE_ID") ?? "";
        if (apiKey == "" || voiceId == "")
        {
            Console.WriteLine("Missing ELEVENLABS_API_KEY / ELEVENLABS_VOICE_ID — skipping voice.");
            return;
        }

        var url = $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}";
        var payload = JsonSerializer.Serialize(new
        {
            text,
            model_id = "eleven_multilingual_v2",
            voice_settings = new { stability = 0.5, similarity_boost = 0.75 }
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("xi-api-key", apiKey);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"ElevenLabs error: {resp.StatusCode}");
            return;
        }

        var mp3Bytes = await resp.Content.ReadAsByteArrayAsync();
        var tempFile = Path.Combine(Path.GetTempPath(), "jerome_welcome.mp3");
        await File.WriteAllBytesAsync(tempFile, mp3Bytes);

        using var reader = new AudioFileReader(tempFile);
        using var output = new WaveOutEvent();
        output.Init(reader);
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing)
            await Task.Delay(200);
    }

    // ---- Focus Cursor via Win32 interop ----
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    const int SW_MAXIMIZE = 3;
    const int SW_MINIMIZE = 6;

    // ---- Close specific windows by title text (used to close just the hub dashboards) ----
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    const uint WM_CLOSE = 0x0010;

    // Closes one specific window by its handle — used for the Claude tab, since we
    // want to close exactly the one Jerome opened, not anything matching by title.
    static void CloseSpecificWindow(ref IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try { PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception ex) { Console.WriteLine($"Couldn't close Claude window: {ex.Message}"); }
        hWnd = IntPtr.Zero; // clear it either way — stale handles shouldn't be reused
    }

    static void CloseWindowsByTitleContains(string titleSubstring)
    {
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                int len = GetWindowTextLength(hWnd);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString().Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))
                    PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); // same as clicking the window's X
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { Console.WriteLine($"Couldn't close window \"{titleSubstring}\": {ex.Message}"); }
    }

    // ---- Auto-tile the four wake-up windows into an even 2x2 grid ----
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    const uint SWP_NOZORDER = 0x0004;
    const int SW_RESTORE = 9;

    static IntPtr FindWindowByTitleContains(string titleSubstring)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (sb.ToString().Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false; // stop enumerating, we found it
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static async Task<IntPtr> WaitForWindowAsync(string titleSubstring, int timeoutMs = 6000)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            var h = FindWindowByTitleContains(titleSubstring);
            if (h != IntPtr.Zero) return h;
            await Task.Delay(250);
            waited += 250;
        }
        return IntPtr.Zero;
    }

    static void PositionWindow(IntPtr hWnd, int x, int y, int w, int h)
    {
        if (hWnd == IntPtr.Zero) return;
        ShowWindow(hWnd, SW_RESTORE); // un-maximize/un-minimize first so resizing actually takes effect
        SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER);
    }

    // Tiles: hologram top-left, hud-dashboard top-right, trading-dashboard bottom-left,
    // Claude tab bottom-right. Matches by window title, same technique as sleep mode's
    // window-closing. Note: matching "Claude" is a loose title match — if you already
    // have another Claude window open elsewhere, tiling could grab that one instead.
    static IntPtr _claudeWindowHandle = IntPtr.Zero;

    static async Task TileWindowsAsync()
    {
        try
        {
            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);
            int halfW = screenW / 2, halfH = screenH / 2;

            var layout = new (string title, int x, int y)[]
            {
                ("Eye View", 0, 0), // God's Eye View — using a short substring since apostrophe
                                     // characters can render inconsistently in window titles
                ("JEROME // OPTIMAL ENGINE", halfW, 0),
                ("JEROME // TRADE DESK", 0, halfH),
                ("Claude", halfW, halfH),
            };

            foreach (var (title, x, y) in layout)
            {
                var hWnd = await WaitForWindowAsync(title);
                PositionWindow(hWnd, x, y, halfW, halfH);
                // Remember exactly which window was "the Claude one" at the moment we
                // opened it, so sleep mode can close that specific window later instead
                // of matching "Claude" broadly (which could grab an unrelated window).
                if (title == "Claude") _claudeWindowHandle = hWnd;
            }
        }
        catch (Exception ex) { Console.WriteLine($"Window tiling failed: {ex.Message}"); }
    }

    static void FocusCursorWindow()
    {
        var proc = Process.GetProcessesByName("Cursor").FirstOrDefault();
        if (proc == null)
        {
            Console.WriteLine("Cursor not running — skipping focus.");
            return;
        }
        ShowWindow(proc.MainWindowHandle, SW_MAXIMIZE);
        SetForegroundWindow(proc.MainWindowHandle);
    }

    // ---- Minimal .env loader (so you can keep secrets out of the code, like the Python version) ----
    // ---- Toast-style notifications, since Jerome usually runs with no visible window ----
    static void Notify(string title, string message)
    {
        try { _notifyIcon?.ShowBalloonTip(4000, title, message, ToolTipIcon.Info); }
        catch (Exception ex) { Console.WriteLine($"Notification failed: {ex.Message}"); }
    }

    static void LoadEnvFile(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed == "" || trimmed.StartsWith("#")) continue;
            var idx = trimmed.IndexOf('=');
            if (idx < 0) continue;
            var key = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..].Trim();
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
