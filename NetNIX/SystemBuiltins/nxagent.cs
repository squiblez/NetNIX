using System;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using NetNIX.Scripting;
using NetNIX.Users;

#include <settingslib>
#include <koboldlib>

/// <summary>
/// nxagent — AI agent daemon for NetNIX.
///
/// Runs in the background as a dedicated user (default: "nxagent") and
/// drives an interactive shell session by feeding the captured terminal
/// output back to a KoboldCpp-compatible AI, then typing the AI's reply
/// as the next shell command. Operators steer the agent by sending mail
/// to the agent user — directives in the mailbox are included in every
/// prompt sent to the AI.
///
/// Start:  daemon start nxagent /sbin/nxagent.cs
/// Stop:   daemon stop nxagent
///
/// Configuration: /etc/nxagent.conf
/// VFS logs:      /var/log/nxagent.log     (events, default on)
///                /var/log/nxagent/session.log (full transcript, default on)
/// Host logs:     logs/nxagent.log         (events, default on)
///                logs/nxagent-session.log (full transcript, default on)
///
/// Sandbox: only standard NixApi calls are used, so no /etc/sandbox.exceptions
/// entries are required.
/// </summary>
public static class NxAgentDaemon
{
    // ?? Configuration ??????????????????????????????????????????????

    private const string ConfigPath = "/etc/nxagent.conf";

    private class NxAgentConfig
    {
        public string AgentUsername = "nxagent";
        public int CommandDelaySeconds = 5;
        public int PromptHistoryLines = 250;
        public int PromptWaitSeconds = 60;
        public int MaxTurns = 0;            // 0 = unlimited
        public int TerminalWidth = 100;
        public int TerminalHeight = 40;
        public bool ConsumeMail = false;    // delete directives after reading
        // Path to a VFS file whose contents replace SystemPrompt at
        // startup. Lets root edit a multi-line prose file instead of
        // cramming the whole prompt onto one config line.
        public string SystemPromptFile = "/etc/nxagent.prompt";
        // Kobold overrides - empty/zero means "use whatever the agent
        // user's ~/.config/kobold.conf says". Setting any of these in
        // /etc/nxagent.conf lets root configure the AI endpoint without
        // having to su to the agent user first.
        public string KoboldEndpoint = "";
        public string KoboldApiKey = "";
        public bool KoboldUseApiKey = false;
        public bool KoboldUseApiKeySet = false;
        public int KoboldMaxLength = 0;
        public double KoboldTemperature = 0;
        public bool KoboldTemperatureSet = false;
        // How long (in seconds) the daemon is willing to wait for a
        // single kobold /api/v1/generate response. Self-hosted models
        // running large prompts on slow hardware can easily take many
        // minutes per turn, so the default is intentionally generous.
        public int KoboldGenerateTimeoutSeconds = 1800; // 30 minutes
        // How often (in seconds) to log a progress line while waiting
        // on a slow kobold response. Set to 0 to disable heartbeats.
        // 120s by default so the operator notices a long-running
        // request quickly; raise it (e.g. to 600) for chatty logs on
        // models that routinely take many minutes per reply.
        public int KoboldHeartbeatSeconds = 120;        // 2 minutes
        // Progressive backoff sequence (in seconds) for AI/network
        // failures. The Nth consecutive failure waits the Nth entry
        // before retrying; once we run off the end of the list we keep
        // using the last value. Default {5, 15, 30, 60} - a one-off
        // glitch costs only 5 seconds, but a sustained outage backs
        // off so we are not hammering a dead endpoint.
        public int[] KoboldRetryDelaysSeconds = new[] { 5, 15, 30, 60 };
        public string SystemPrompt =
            "You are an autonomous AI operator running inside NetNIX, a custom " +
            "UNIX-like environment built on .NET 8. NetNIX is NOT Linux. Many " +
            "common Linux commands and flags do NOT exist here.\n" +
            "\n" +
            "You are logged in as a non-root user and you are interacting with " +
            "a real interactive shell (nsh). Below you will see:\n" +
            "  1. Operator directives - messages your human operator sent to " +
            "your mailbox.\n" +
            "  2. Recent shell history - the last several prompts, commands you " +
            "issued, and their output, exactly as they appeared in the terminal. " +
            "USE THIS HISTORY: do not repeat commands that already failed.\n" +
            "\n" +
            "Reply with EXACTLY ONE shell command on ONE line. No markdown, " +
            "no code fences, no commentary, no leading '$' or '#'. Just the " +
            "command itself.\n" +
            "\n" +
            "Available NetNIX commands (this is the canonical list):\n" +
            "  Files / dirs:  ls  cd  pwd  cat  echo  mkdir  rmdir  touch  rm  " +
            "mv  cp  find  grep  wc  head  tail  sort  uniq  stat  file  du  df\n" +
            "  Process / system: whoami  id  env  uname  date  ps  who  sleep  " +
            "true  false  yes  exit\n" +
            "  Networking:    curl  wget  fetch  ping  hostname\n" +
            "  Mail:          mail (mail -r N reads message N, mail -d N deletes, " +
            "mail -s SUBJ USER sends; check it for new directives every few turns)\n" +
            "  Discovery:     help (lists every command), man <cmd> exists but is " +
            "interactive - DO NOT RUN IT.\n" +
            "\n" +
            "Things that DO NOT exist or that you must NEVER use:\n" +
            "  - sudo, su, ssh, apt, yum, systemctl, service - none of these exist\n" +
            "  - Linux-only flags such as --color, --human-readable, ls -la " +
            "(use 'ls -l' or 'ls'), grep -P, find -printf\n" +
            "  - Interactive programs: edit, less, more, kobold (chat), man, " +
            "vi, nano - they will hang because you cannot send keystrokes\n" +
            "  - Long-running commands without a clear purpose; never start a " +
            "daemon or background loop\n" +
            "\n" +
            "Workflow tips:\n" +
            "  - If you are unsure whether a command exists, run 'help' first.\n" +
            "  - 'mail' (no args) lists your inbox; read directives with -r.\n" +
            "  - To report results back to the operator, use " +
            "'echo TEXT | mail -s SUBJECT root' (or whichever username sent " +
            "you the directive).\n" +
            "  - If you have nothing useful to do, run 'sleep 10' rather than " +
            "making something up.\n";
        // Logging
        public bool LogEvents = true;
        public bool LogSessions = true;
        public bool LogRawShell = true;
        public bool HostLogEvents = true;
        public bool HostLogSessions = true;
        public bool HostLogRawShell = true;
    }

    private static NxAgentConfig _config = new NxAgentConfig();

    private static NxAgentConfig LoadConfig(NixApi api)
    {
        var cfg = new NxAgentConfig();
        if (!api.IsFile(ConfigPath)) return cfg;

        try
        {
            string content = api.ReadText(ConfigPath);
            foreach (var rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();

                switch (key)
                {
                    case "agent_username":  cfg.AgentUsername = val; break;
                    case "command_delay":
                        if (int.TryParse(val, out int cd) && cd >= 0) cfg.CommandDelaySeconds = cd;
                        break;
                    case "prompt_history_lines":
                        if (int.TryParse(val, out int pl) && pl > 0) cfg.PromptHistoryLines = pl;
                        break;
                    case "prompt_wait_seconds":
                        if (int.TryParse(val, out int pw) && pw > 0) cfg.PromptWaitSeconds = pw;
                        break;
                    case "max_turns":
                        if (int.TryParse(val, out int mt) && mt >= 0) cfg.MaxTurns = mt;
                        break;
                    case "terminal_width":
                        if (int.TryParse(val, out int tw) && tw >= 40) cfg.TerminalWidth = tw;
                        break;
                    case "terminal_height":
                        if (int.TryParse(val, out int th) && th >= 10) cfg.TerminalHeight = th;
                        break;
                    case "consume_mail":
                        cfg.ConsumeMail = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "system_prompt":
                        if (val.Length > 0) cfg.SystemPrompt = val;
                        break;
                    case "system_prompt_file":
                        cfg.SystemPromptFile = val;
                        break;
                    case "kobold_endpoint":
                        cfg.KoboldEndpoint = val;
                        break;
                    case "kobold_api_key":
                        cfg.KoboldApiKey = val;
                        break;
                    case "kobold_use_api_key":
                        cfg.KoboldUseApiKey = val.Equals("true", System.StringComparison.OrdinalIgnoreCase);
                        cfg.KoboldUseApiKeySet = true;
                        break;
                    case "kobold_max_length":
                        if (int.TryParse(val, out int kml) && kml > 0) cfg.KoboldMaxLength = kml;
                        break;
                    case "kobold_temperature":
                        if (double.TryParse(val, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double kt))
                        {
                            cfg.KoboldTemperature = kt;
                            cfg.KoboldTemperatureSet = true;
                        }
                        break;
                    case "kobold_generate_timeout_seconds":
                        if (int.TryParse(val, out int kgts) && kgts > 0)
                            cfg.KoboldGenerateTimeoutSeconds = kgts;
                        break;
                    case "kobold_heartbeat_seconds":
                        if (int.TryParse(val, out int khb) && khb >= 0)
                            cfg.KoboldHeartbeatSeconds = khb;
                        break;
                    case "kobold_retry_delays_seconds":
                        // Comma-separated list of integers, e.g. "5,15,30,60".
                        var parts = val.Split(',', StringSplitOptions.RemoveEmptyEntries
                            | StringSplitOptions.TrimEntries);
                        var parsed = new List<int>();
                        foreach (var p in parts)
                            if (int.TryParse(p, out int n) && n >= 0) parsed.Add(n);
                        if (parsed.Count > 0) cfg.KoboldRetryDelaysSeconds = parsed.ToArray();
                        break;
                    case "log_events":
                        cfg.LogEvents = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "log_sessions":
                        cfg.LogSessions = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "log_raw_shell":
                        cfg.LogRawShell = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "host_log_events":
                        cfg.HostLogEvents = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "host_log_sessions":
                        cfg.HostLogSessions = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "host_log_raw_shell":
                        cfg.HostLogRawShell = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }
        }
        catch { /* keep defaults */ }

        return cfg;
    }

    // ?? Daemon entry point ?????????????????????????????????????????

    // Rolling history of everything that has appeared in the inner shell
    // (prompts, commands the AI sent, and command output) since the
    // daemon started. Trimmed to the last PromptHistoryLines lines on
    // each AI prompt build. Reset at every Daemon() entry so a stop/
    // start cycle gives the AI a clean slate.
    private static readonly StringBuilder _history = new StringBuilder();
    private static readonly object _historyLock = new object();
    private const int HistoryHardCapChars = 200_000;

    // Loop-detection state. The agent has a pronounced tendency to
    // hammer the same command repeatedly when it cannot tell that the
    // shell silently rejected its syntax (heredocs, unsupported flags,
    // etc.). We watch the most recent issued command and intervene
    // when the AI clearly is not making progress.
    private static string _lastIssuedCommand = "";
    private static int _commandRepeatCount = 0;
    private static bool _stuckAlertSent = false;
    private const int RepeatWarnThreshold     = 2; // 2nd repeat -> nudge
    private const int RepeatBlockThreshold    = 3; // 3rd repeat -> skip + strong nudge
    private const int RepeatEscalateThreshold = 5; // 5th repeat -> mail root once

    // Consecutive AI / network failures. Helps the operator distinguish
    // a one-off blip (model swapped, brief net glitch) from a sustained
    // outage that needs attention. Reset to 0 on the next successful
    // turn; mail root once the threshold is crossed.
    private static int _consecutiveFailures = 0;
    private static bool _failureAlertSent = false;
    private const int FailureAlertThreshold = 5;

    public static int Daemon(NixApi api, string[] args)
    {
        _config = LoadConfig(api);

        // Reset history from any previous daemon run within the same process.
        lock (_historyLock) _history.Clear();

        // Optional system-prompt override from a multi-line VFS file -
        // much friendlier to edit than a single-line conf entry.
        if (!string.IsNullOrEmpty(_config.SystemPromptFile) && api.IsFile(_config.SystemPromptFile))
        {
            try
            {
                string fromFile = api.ReadText(_config.SystemPromptFile);
                if (!string.IsNullOrWhiteSpace(fromFile))
                    _config.SystemPrompt = fromFile.TrimEnd();
            }
            catch { /* fall back to whatever SystemPrompt already holds */ }
        }

        // Locate the agent user.
        var userMgr = api.GetUserManager();
        UserRecord agentUser = null;
        foreach (var u in userMgr.Users)
        {
            if (u.Username == _config.AgentUsername) { agentUser = u; break; }
        }

        if (agentUser == null)
        {
            string msg = "agent user '" + _config.AgentUsername + "' does not exist - cannot start";
            Console.Error.WriteLine("nxagent: " + msg);
            LogEvent(api, msg);
            return 1;
        }

        // Build a NixApi instance running as the agent user. This is
        // ONLY used for tasks that should happen in the agent's own
        // permission context (mailbox reads, etc.). The kobold client
        // deliberately uses the daemon's root api below so that
        // 'kobold --set' run by root is honoured.
        var agentApi = new NixApi(api.GetFileSystem(), userMgr,
            agentUser.Uid, agentUser.Gid, agentUser.Username, agentUser.HomeDirectory);
        agentApi.SetRuntime(api.GetScriptRunner(), api.GetDaemonManager());

        // Initialise kobold using the daemon's (root) api. This means
        // settings come from /root/.config/kobold.conf (which is what
        // 'kobold --set' edits when run by root) and from the system-
        // wide /etc/opt/kobold.conf - NOT from the agent user's home,
        // which would otherwise be auto-seeded with the localhost
        // default the very first time the daemon ran.
        var kobold = new KoboldApi(api);
        string koboldSource = "root user settings (/root/.config/kobold.conf)";

        // Apply /etc/nxagent.conf overrides on top - these always win.
        if (!string.IsNullOrEmpty(_config.KoboldEndpoint))
        {
            kobold.Endpoint = _config.KoboldEndpoint;
            koboldSource = "/etc/nxagent.conf override";
        }
        if (!string.IsNullOrEmpty(_config.KoboldApiKey))
            kobold.ApiKey = _config.KoboldApiKey;
        if (_config.KoboldUseApiKeySet)
            kobold.UseApiKey = _config.KoboldUseApiKey;
        if (_config.KoboldMaxLength > 0)
            kobold.MaxLength = _config.KoboldMaxLength;
        if (_config.KoboldTemperatureSet)
            kobold.Temperature = _config.KoboldTemperature;

        if (!kobold.IsAvailable())
        {
            string msg = "kobold endpoint '" + kobold.Endpoint + "' is not reachable - aborting startup (source: " + koboldSource + ")";
            Console.Error.WriteLine("nxagent: " + msg);
            Console.Error.WriteLine("nxagent: fix with 'kobold --set endpoint http://HOST:PORT' as root,");
            Console.Error.WriteLine("nxagent: or set kobold_endpoint in /etc/nxagent.conf");
            LogEvent(api, msg);
            return 1;
        }

        Console.WriteLine("nxagent: starting as user '" + agentUser.Username + "' (uid " + agentUser.Uid + ")");
        Console.WriteLine("nxagent: kobold endpoint " + kobold.Endpoint + " (source: " + koboldSource + ")");
        LogEvent(api, "nxagent started as '" + agentUser.Username + "', kobold=" + kobold.Endpoint + " (" + koboldSource + ")");

        // Shared I/O between the AI loop and the inner shell session.
        var inputQueue = new BlockingCollection<string>();
        var outputBuffer = new StringBuilder();
        var outputLock = new object();
        var shellExited = new ManualResetEventSlim(false);

        var captureWriter = new CaptureWriter(outputBuffer, outputLock);
        var queuedReader  = new QueuedReader(inputQueue);

        var token = api.DaemonToken;

        // Spawn the inner shell on its own thread.
        var shellThread = new Thread(() =>
        {
            try
            {
                NetNIX.Shell.SessionIO.Enter(queuedReader, captureWriter,
                    isRemote: true,
                    terminalWidth: _config.TerminalWidth,
                    terminalHeight: _config.TerminalHeight);
                try
                {
                    var shell = new NetNIX.Shell.NixShell(
                        api.GetFileSystem(),
                        api.GetUserManager(),
                        api.GetScriptRunner(),
                        api.GetDaemonManager(),
                        agentUser,
                        isRemote: true);
                    shell.Run();
                }
                finally
                {
                    NetNIX.Shell.SessionIO.Leave();
                }
            }
            catch (Exception ex)
            {
                LogEvent(api, "shell thread crashed: " + ex.Message);
            }
            finally
            {
                shellExited.Set();
            }
        })
        {
            Name = "nxagent-shell",
            IsBackground = true,
        };
        shellThread.Start();

        // Wait for the first shell prompt before starting the AI loop.
        string promptUser = agentUser.Username;
        if (!WaitForPrompt(outputBuffer, outputLock, promptUser, _config.PromptWaitSeconds, token))
        {
            LogEvent(api, "shell never produced a prompt - aborting");
            inputQueue.CompleteAdding();
            return 1;
        }

        // Drain and log the welcome/initial output.
        string startup = DrainBuffer(outputBuffer, outputLock);
        LogSession(api, "[STARTUP]\n" + startup);
        AppendHistory(api, startup);

        int turn = 0;
        while (!token.IsCancellationRequested)
        {
            if (_config.MaxTurns > 0 && turn >= _config.MaxTurns)
            {
                LogEvent(api, "max_turns (" + _config.MaxTurns + ") reached - stopping");
                break;
            }

            // Wrap the entire turn body so that NO unhandled exception
            // can silently kill the daemon thread. Anything that escapes
            // the inner try blocks (a bad string operation, an HTTP
            // library throwing during an interleaved write to a host
            // stream, an OOM during prompt building, etc.) is caught
            // at the bottom of the loop, logged with full type/message/
            // stack, and the loop continues after a short backoff.
            try
            {
                turn++;

            // Collect operator directives from mail.
            string directives = ReadMailDirectives(agentApi);

            // Drain whatever is in the output buffer (output produced
            // by the previous command + the new prompt) and append to
            // the rolling history so the AI sees its own past commands
            // and their results, not just the most recent drain.
            string lastOutput = DrainBuffer(outputBuffer, outputLock);
            AppendHistory(api, lastOutput);

            // Pull the last N lines of the rolling history for the AI.
            string historyTail = GetHistoryTail(_config.PromptHistoryLines);

            // Build the prompt and ask the AI for the next command.
            string prompt = BuildAgentPrompt(_config.SystemPrompt, directives, historyTail, turn);
            LogSession(api, "[TURN " + turn + " PROMPT]\n" + prompt);

            // Self-hosted models on slow hardware can take many minutes
            // per response. Use the configured generate timeout (default
            // 30 min) and run a heartbeat thread that logs every
            // KoboldHeartbeatSeconds so the operator can see the agent
            // is alive and waiting, not crashed.
            string reply = null;
            var generateDone = new ManualResetEventSlim(false);
            var generateStart = DateTime.UtcNow;
            int turnSnapshot = turn;
            Thread heartbeat = null;
            if (_config.KoboldHeartbeatSeconds > 0)
            {
                heartbeat = new Thread(() =>
                {
                    var interval = TimeSpan.FromSeconds(_config.KoboldHeartbeatSeconds);
                    while (!generateDone.Wait(interval))
                    {
                        int mins = (int)(DateTime.UtcNow - generateStart).TotalMinutes;
                        LogEvent(api, "kobold: still waiting for response after " + mins
                            + "m (turn " + turnSnapshot + ", limit "
                            + (_config.KoboldGenerateTimeoutSeconds / 60) + "m)");
                    }
                })
                { IsBackground = true, Name = "nxagent-kobold-heartbeat" };
                heartbeat.Start();
            }

            // Visible "request started" line so the operator can see in
            // /var/log/nxagent.log (and on the host log) the moment each
            // kobold call begins. Without this, a slow generation looks
            // indistinguishable from a wedged daemon.
            LogEvent(api, "kobold: sending prompt for turn " + turn
                + " (" + prompt.Length + " chars, timeout "
                + _config.KoboldGenerateTimeoutSeconds + "s)");

            try { reply = kobold.GenerateWithTimeout(prompt, _config.KoboldGenerateTimeoutSeconds); }
            catch (Exception ex) { LogEvent(api, "kobold error: " + ex.Message); }
            finally
            {
                generateDone.Set();
                heartbeat?.Join(TimeSpan.FromSeconds(2));
            }

            // Pair the "sending" line with a "received" line so timings
            // are visible at a glance.
            int elapsedSec = (int)(DateTime.UtcNow - generateStart).TotalSeconds;
            if (reply != null)
                LogEvent(api, "kobold: received reply for turn " + turn
                    + " (" + reply.Length + " chars, took " + elapsedSec + "s)");
            else
                LogEvent(api, "kobold: NO reply for turn " + turn
                    + " (gave up after " + elapsedSec + "s)");

            if (string.IsNullOrWhiteSpace(reply))
            {
                _consecutiveFailures++;
                // Pick the Nth backoff (1-indexed), clamping to the last
                // entry once we run off the end. So with default
                // {5, 15, 30, 60}: failure 1 -> 5s, 2 -> 15s, 3 -> 30s,
                // 4 and beyond -> 60s.
                int[] delays = _config.KoboldRetryDelaysSeconds;
                int delaySec = delays[Math.Min(_consecutiveFailures, delays.Length) - 1];
                LogEvent(api, "no response from AI on turn " + turn
                    + ", sleeping " + delaySec + "s (consecutive failures: "
                    + _consecutiveFailures + ")");
                AppendHistory(api, "nxagent: no response from AI on turn " + turn
                    + " (kobold returned empty or timed out, sleeping " + delaySec + "s)\n");

                // Escalate once when the network / model has been failing
                // for several turns in a row. Daemon keeps trying so it
                // recovers automatically once kobold comes back; the
                // mail just makes sure the operator knows.
                if (_consecutiveFailures >= FailureAlertThreshold && !_failureAlertSent)
                {
                    SendFailureAlert(agentApi, _consecutiveFailures);
                    _failureAlertSent = true;
                }

                if (token.WaitHandle.WaitOne(delaySec * 1000)) break;
                continue;
            }

            // Got a real reply - reset the failure counter so the next
            // failure starts a fresh streak.
            if (_consecutiveFailures > 0)
            {
                LogEvent(api, "AI responded again after " + _consecutiveFailures
                    + " consecutive failure(s)");
                _consecutiveFailures = 0;
                _failureAlertSent = false;
            }

            string command = ExtractCommand(reply);
            if (command.Length == 0)
            {
                LogSession(api, "[TURN " + turn + " EMPTY-REPLY]\n" + reply);
                // Surface in shell.log too. Truncate and flatten newlines
                // so it stays readable in the transcript view.
                string snippet = reply.Replace("\r", "").Replace("\n", " | ").Trim();
                if (snippet.Length > 200) snippet = snippet.Substring(0, 200) + "...";
                if (snippet.Length == 0) snippet = "<blank>";
                AppendHistory(api, "nxagent: AI reply produced no executable command on turn "
                    + turn + " (raw reply: " + snippet + ")\n");
                if (token.WaitHandle.WaitOne(_config.CommandDelaySeconds * 1000)) break;
                continue;
            }

            LogSession(api, "[TURN " + turn + " AI-REPLY]\n" + reply);
            LogSession(api, "[TURN " + turn + " COMMAND]\n" + command);

            // ?? Loop / stuck-AI detection ???????????????????????????
            // Compare against the most recently issued command. When the
            // AI re-sends the exact same line, it usually means the
            // shell silently swallowed unsupported syntax (heredocs,
            // Linux-only flags, etc.) and the AI cannot tell. Warn,
            // then block, then mail root.
            string normalized = command.Trim();
            if (normalized.Length > 0 && normalized == _lastIssuedCommand)
            {
                _commandRepeatCount++;
            }
            else
            {
                _lastIssuedCommand = normalized;
                _commandRepeatCount = 1;
                _stuckAlertSent = false;
            }

            if (_commandRepeatCount >= RepeatBlockThreshold)
            {
                // Hard stop: do NOT actually run the command again.
                // Log the rejected command first so shell.log records
                // EVERY AI attempt (the operator explicitly needs to
                // see them all), then inject a strong nudge so the AI
                // sees the warning on its next turn and (hopefully)
                // changes course.
                AppendHistory(api, command + "\n");
                string nudge = "nxagent: BLOCKED - you have issued '" + normalized + "' "
                    + _commandRepeatCount + " times in a row. The output is identical each time, "
                    + "which means the shell silently accepted the line but did nothing useful. "
                    + "Common causes: heredoc syntax (<<'EOF') is NOT supported, Linux-only "
                    + "flags (--color, ls -la), or trying to run an interactive program. "
                    + "You MUST issue a completely different command this turn. To create a "
                    + "file with multi-line content use a single 'echo' with \\n escapes and "
                    + "the > redirection, or use 'edit' (which IS interactive and you cannot "
                    + "drive). If you have nothing useful to do, run 'sleep 10'.\n";
                AppendHistory(api, nudge);
                LogEvent(api, "loop-blocked: '" + normalized + "' x" + _commandRepeatCount);

                // Escalate to root once after a few blocked iterations.
                if (_commandRepeatCount >= RepeatEscalateThreshold && !_stuckAlertSent)
                {
                    SendStuckAlert(agentApi, normalized, _commandRepeatCount);
                    _stuckAlertSent = true;
                }

                if (token.WaitHandle.WaitOne(_config.CommandDelaySeconds * 1000)) break;
                continue;
            }

            if (_commandRepeatCount >= RepeatWarnThreshold)
            {
                // Soft warning: still let the command run once more so
                // the AI sees its result, but flag the repetition.
                string warn = "nxagent: WARNING - you just issued '" + normalized
                    + "' " + _commandRepeatCount + " turns in a row. The output is unchanged. "
                    + "Pick a DIFFERENT command next turn. If you cannot tell why this is not "
                    + "working, the syntax may be unsupported (e.g. heredocs are NOT supported "
                    + "in nsh - use 'echo \"line1\\nline2\" > file' instead).\n";
                AppendHistory(api, warn);
                LogEvent(api, "loop-warn: '" + normalized + "' x" + _commandRepeatCount);
            }

            // Refuse to feed dangerous self-destruct commands. The agent
            // can be useful, but should not silently nuke the rootfs.
            if (IsForbidden(command))
            {
                LogEvent(api, "blocked forbidden command: " + command);
                AppendHistory(api, command + "\nnxagent: blocked forbidden command\n");
                lock (outputLock)
                    outputBuffer.Append("nxagent: blocked forbidden command\n");
                if (token.WaitHandle.WaitOne(_config.CommandDelaySeconds * 1000)) break;
                continue;
            }

            // Write the AI's command into the rolling history (and the
            // raw shell log) as if a human had typed it after the prompt
            // that was just drained.
            AppendHistory(api, command + "\n");
            try { inputQueue.Add(command); }
            catch { break; }

            // Wait for the shell to finish processing and re-prompt.
            if (!WaitForPrompt(outputBuffer, outputLock, promptUser, _config.PromptWaitSeconds, token))
            {
                LogEvent(api, "command did not return to prompt within " + _config.PromptWaitSeconds + "s: " + command);
                // Flush whatever output we have so far for this turn.
                string partial = DrainBuffer(outputBuffer, outputLock);
                LogSession(api, "[TURN " + turn + " TIMEOUT-OUTPUT]\n" + partial);
                AppendHistory(api, partial + "\nnxagent: command timed out after " +
                    _config.PromptWaitSeconds + "s\n");
            }

            int delayMs = _config.CommandDelaySeconds * 1000;
            if (delayMs > 0)
            {
                if (token.WaitHandle.WaitOne(delayMs)) break;
            }
            }
            catch (Exception ex)
            {
                LogEvent(api, "turn " + turn + " crashed: " + ex.GetType().Name
                    + ": " + ex.Message);
                LogSession(api, "[TURN " + turn + " CRASHED]\n"
                    + ex.GetType().FullName + ": " + ex.Message + "\n" + ex.StackTrace);
                AppendHistory(api, "nxagent: turn " + turn + " crashed ("
                    + ex.GetType().Name + ": " + ex.Message
                    + "). Daemon recovering and continuing.\n");
                // Short backoff so a crash loop does not spin the CPU.
                if (token.WaitHandle.WaitOne(5000)) break;
            }
        }

        // Shutdown: send 'exit' so the inner shell terminates cleanly.
        AppendHistory(api, "exit\n");
        try { inputQueue.Add("exit"); } catch { }
        try { inputQueue.CompleteAdding(); } catch { }
        shellExited.Wait(TimeSpan.FromSeconds(5));

        // Capture any final output the shell produced as it exited.
        string tail = DrainBuffer(outputBuffer, outputLock);
        if (tail.Length > 0) AppendHistory(api, tail);

        Console.WriteLine("nxagent: stopped");
        LogEvent(api, "nxagent stopped after " + turn + " turn(s)");
        return 0;
    }

    // ?? Prompt building ????????????????????????????????????????????

    private static string BuildAgentPrompt(string systemPrompt, string directives, string shellHistory, int turn)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### System:");
        sb.AppendLine(systemPrompt);
        sb.AppendLine();
        sb.AppendLine("### Operator directives (your mailbox):");
        sb.AppendLine(string.IsNullOrWhiteSpace(directives) ? "(no new directives)" : directives.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("### Recent shell history (turn " + turn + ", oldest first, ends with the prompt awaiting your next command):");
        sb.AppendLine(string.IsNullOrWhiteSpace(shellHistory) ? "(no history yet)" : shellHistory.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("### Your next shell command (one line, no markdown):");
        return sb.ToString();
    }

    private static string ReadMailDirectives(NixApi agentApi)
    {
        string mbox = "/var/mail/" + agentApi.Username;
        if (!agentApi.IsFile(mbox)) return "";
        try
        {
            string text = agentApi.ReadText(mbox);
            if (_config.ConsumeMail && text.Length > 0)
            {
                agentApi.WriteText(mbox, "");
                agentApi.Save();
            }
            return text;
        }
        catch { return ""; }
    }

    private static string TrimToLastLines(string text, int maxLines)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Split('\n');
        if (lines.Length <= maxLines) return text;
        return "...[" + (lines.Length - maxLines) + " earlier line(s) trimmed]...\n" +
               string.Join("\n", lines, lines.Length - maxLines, maxLines);
    }

    private static string ExtractCommand(string reply)
    {
        if (reply == null) return "";
        // Strip code fences and surrounding whitespace.
        reply = reply.Replace("\r", "");
        var lines = reply.Split('\n');
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("```") || line.StartsWith("//") || line.StartsWith("#")) continue;
            // Strip a leading shell prompt indicator like "$ " or "# ".
            if (line.StartsWith("$ ") || line.StartsWith("# ")) line = line.Substring(2).Trim();
            return line;
        }
        return "";
    }

    /// <summary>
    /// Notify root that the AI / kobold endpoint has been unresponsive
    /// for several consecutive turns. Best-effort: failures are
    /// swallowed so the alerting path itself can never crash the
    /// daemon.
    /// </summary>
    private static void SendFailureAlert(NixApi agentApi, int failures)
    {
        try
        {
            const string mboxRoot = "/var/mail/root";
            string date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string entry =
                "=== From: " + agentApi.Username +
                "  Date: " + date +
                "  Subject: nxagent: " + failures + " consecutive AI failures ===\n" +
                "My last " + failures + " turns received no response from the configured " +
                "kobold endpoint (timeout, network error, or empty reply). The daemon " +
                "is still alive and will keep retrying every 30 seconds; once kobold " +
                "returns a real reply this counter resets and you will see a " +
                "\"AI responded again\" line in /var/log/nxagent.log.\n\n" +
                "If you do NOT see successful turns soon, check:\n" +
                "  - the kobold endpoint URL in nxagent's user config\n" +
                "  - that the model server is reachable from this host\n" +
                "  - logs/nxagent.log on the host for repeated 'kobold error' lines\n";
            if (agentApi.IsFile(mboxRoot))
                agentApi.AppendText(mboxRoot, entry);
            agentApi.Save();
        }
        catch { /* best-effort - never let alerting kill the daemon */ }
    }

    /// <summary>
    /// Append an alert to root's mailbox notifying the operator that
    /// the agent is stuck repeating the same command.
    /// user's NixApi (delivery to /var/mail/root works because that
    /// file is rw-rw-rw- by mail --setup convention). Best-effort -
    /// any failure is swallowed so loop-detection itself never crashes
    /// the daemon.
    /// </summary>
    private static void SendStuckAlert(NixApi agentApi, string command, int repeats)
    {
        try
        {
            const string mboxRoot = "/var/mail/root";
            string date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string entry =
                "=== From: " + agentApi.Username +
                "  Date: " + date +
                "  Subject: nxagent is stuck repeating a command ===\n" +
                "I have issued the following command " + repeats + " times in a row " +
                "and the shell output is identical each turn:\n\n" +
                "    " + command + "\n\n" +
                "This usually means the syntax is silently unsupported (heredocs, " +
                "Linux-only flags, or interactive programs). The loop detector has " +
                "blocked further attempts and is forcing me to try something else. " +
                "Please review my recent shell.log and consider sending a clearer " +
                "directive.\n";
            if (agentApi.IsFile(mboxRoot))
                agentApi.AppendText(mboxRoot, entry);
            agentApi.Save();
        }
        catch { /* best-effort - never let alerting kill the daemon */ }
    }

    private static bool IsForbidden(string command)
    {
        // Conservative blocklist - protects the rootfs from one-shot disasters.
        string c = command.Trim().ToLowerInvariant();
        if (c.StartsWith("rm -rf /") || c == "rm -rf /") return true;
        if (c.StartsWith("rm -rf /*")) return true;
        if (c.StartsWith("__reset__")) return true;
        if (c.Contains(":(){:|:&};:")) return true;
        return false;
    }

    // ?? Prompt-aware buffer waiting ????????????????????????????????

    private static bool WaitForPrompt(StringBuilder buf, object bufLock, string username, int waitSeconds, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (token.IsCancellationRequested) return false;
            if (LooksLikePrompt(buf, bufLock, username)) return true;
            if (token.WaitHandle.WaitOne(150)) return false;
        }
        return false;
    }

    private static bool LooksLikePrompt(StringBuilder buf, object bufLock, string username)
    {
        // Prompt format from NixShell: "<user>@netnix:<cwd>$ " (or "# " for root).
        string snapshot;
        lock (bufLock) snapshot = buf.ToString();
        int len = snapshot.Length;
        if (len < 4) return false;
        // Must end with "$ " or "# ".
        char last = snapshot[len - 1];
        char prev = snapshot[len - 2];
        if (last != ' ' || (prev != '$' && prev != '#')) return false;
        // And the username/host marker must appear after the last newline.
        int lastNl = snapshot.LastIndexOf('\n');
        string lastLine = (lastNl >= 0) ? snapshot.Substring(lastNl + 1) : snapshot;
        return lastLine.StartsWith(username + "@netnix:");
    }

    private static string DrainBuffer(StringBuilder buf, object bufLock)
    {
        lock (bufLock)
        {
            string s = buf.ToString();
            buf.Clear();
            return s;
        }
    }

    // ?? Rolling history (fed to the AI + raw shell log) ????????????

    /// <summary>
    /// Append text to the rolling shell history (which is what the AI
    /// sees on every turn) AND mirror it to the raw shell log files.
    /// Use this for everything that should appear in the transcript:
    /// captured shell output, commands the AI sent, synthetic notices.
    /// </summary>
    private static void AppendHistory(NixApi api, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_historyLock)
        {
            _history.Append(text);
            // Keep the underlying buffer from growing unbounded over a
            // long-running daemon. The actual prompt sent to the AI is
            // already trimmed to PromptHistoryLines, so this cap is just
            // a memory safety net.
            if (_history.Length > HistoryHardCapChars)
                _history.Remove(0, _history.Length - HistoryHardCapChars);
        }
        LogRawShell(api, text);
    }

    /// <summary>
    /// Return the most recent <paramref name="maxLines"/> lines of the
    /// rolling history, with a "...[N earlier line(s) trimmed]..." marker
    /// at the top if anything was dropped.
    /// </summary>
    private static string GetHistoryTail(int maxLines)
    {
        string snap;
        lock (_historyLock) snap = _history.ToString();
        return TrimToLastLines(snap, maxLines);
    }

    // ?? I/O glue between AI loop and inner shell ???????????????????

    private class QueuedReader : System.IO.TextReader
    {
        private readonly BlockingCollection<string> _queue;
        public QueuedReader(BlockingCollection<string> q) { _queue = q; }

        public override string ReadLine()
        {
            try { return _queue.Take(); }
            catch (ObjectDisposedException) { return null; }
            catch (InvalidOperationException) { return null; } // CompleteAdding called
        }

        public override int Read() => -1;
        public override int Peek() => -1;
    }

    private class CaptureWriter : System.IO.TextWriter
    {
        private readonly StringBuilder _buf;
        private readonly object _lock;
        public override Encoding Encoding => Encoding.UTF8;

        public CaptureWriter(StringBuilder buf, object lockObj) { _buf = buf; _lock = lockObj; }

        public override void Write(char value)
        { lock (_lock) _buf.Append(value); }

        public override void Write(string value)
        { if (value == null) return; lock (_lock) _buf.Append(value); }

        public override void Write(char[] buffer, int index, int count)
        { if (buffer == null) return; lock (_lock) _buf.Append(buffer, index, count); }

        public override void WriteLine()
        { lock (_lock) _buf.Append('\n'); }

        public override void WriteLine(string value)
        { lock (_lock) { if (value != null) _buf.Append(value); _buf.Append('\n'); } }

        public override void Flush() { /* in-memory */ }
    }

    // ?? Logging ????????????????????????????????????????????????????

    private static void LogEvent(NixApi api, string message)
    {
        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string line = "[" + ts + "] " + message + "\n";

        if (_config.LogEvents)
        {
            try
            {
                bool isNew = !api.IsFile("/var/log/nxagent.log");
                api.AppendText("/var/log/nxagent.log", line);
                if (isNew) api.Chmod("/var/log/nxagent.log", "rw-------");
                api.Save();
            }
            catch { }
        }

        if (_config.HostLogEvents)
        {
            try { api.HostLog("nxagent", message); } catch { }
        }
    }

    private static void LogSession(NixApi api, string message)
    {
        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string line = "[" + ts + "] " + message + "\n";

        if (_config.LogSessions)
        {
            try
            {
                if (!api.IsDirectory("/var/log/nxagent"))
                {
                    api.CreateDir("/var/log/nxagent");
                    api.Chmod("/var/log/nxagent", "rwx------");
                }
                string path = "/var/log/nxagent/session.log";
                bool isNew = !api.IsFile(path);
                api.AppendText(path, line);
                if (isNew) api.Chmod(path, "rw-------");
                api.Save();
            }
            catch { }
        }

        if (_config.HostLogSessions)
        {
            try { api.HostLogRaw("nxagent-session", line); } catch { }
        }
    }

    /// <summary>
    /// Append plain text (no timestamps, no tags) to the raw shell
    /// transcript so the log reads like a real shell session: prompt,
    /// command, output, prompt, command, output, ...
    /// </summary>
    private static void LogRawShell(NixApi api, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (_config.LogRawShell)
        {
            try
            {
                if (!api.IsDirectory("/var/log/nxagent"))
                {
                    api.CreateDir("/var/log/nxagent");
                    api.Chmod("/var/log/nxagent", "rwx------");
                }
                string path = "/var/log/nxagent/shell.log";
                bool isNew = !api.IsFile(path);
                api.AppendText(path, text);
                if (isNew) api.Chmod(path, "rw-------");
                api.Save();
            }
            catch { }
        }

        if (_config.HostLogRawShell)
        {
            try { api.HostLogRaw("nxagent-shell", text); } catch { }
        }
    }
}
