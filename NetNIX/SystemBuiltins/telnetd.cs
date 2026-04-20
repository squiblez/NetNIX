using System;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using NetNIX.Scripting;

/// <summary>
/// telnetd — Telnet server daemon for NetNIX.
///
/// Allows remote users to connect via any standard Telnet client and
/// access the full NetNIX shell environment. Multiple simultaneous
/// sessions are supported — each connection gets its own login prompt
/// and shell instance, all sharing the same virtual filesystem.
///
/// Start:  daemon start telnetd /sbin/telnetd.cs [port]
/// Stop:   daemon stop telnetd
///
/// Default port: 2323
///
/// Sandbox exceptions required in /etc/sandbox.exceptions:
///   telnetd  System.Net
///   telnetd  System.Net.Sockets
///   telnetd  TcpListener(
///   telnetd  TcpClient(
///   telnetd  Socket(
/// </summary>
public static class TelnetDaemon
{
    // Track active sessions for the 'who' display
    private static readonly List<SessionInfo> _sessions = new();
    private static readonly object _sessionsLock = new();

    private class SessionInfo
    {
        public int Id;
        public string Username = "(login)";
        public string RemoteEndpoint = "";
        public DateTime ConnectedAt;
    }

    // ?? Configuration ??????????????????????????????????????????????

    private const string ConfigPath = "/etc/telnetd.conf";

    /// <summary>
    /// Parsed telnetd configuration values.
    /// </summary>
    private class TelnetConfig
    {
        public int Port = 2323;
        public int TerminalWidth = 80;
        public int TerminalHeight = 24;
        public int MaxSessions = 8;
        public int MaxLoginAttempts = 5;
        public int IdleTimeoutMinutes = 30;
        public string LoginBanner = "NetNIX Remote Access";
        public string WelcomeMessage = "Welcome {user}! (remote session #{session})";
        // Logging
        public bool LogEvents = true;
        public bool LogSessions = false;
        public bool HostLogEvents = false;
        public bool HostLogSessions = false;
    }

    private static TelnetConfig LoadConfig(NixApi api)
    {
        var cfg = new TelnetConfig();

        // Config is installed by FactoryFiles to /etc/telnetd.conf.
        // If missing, just use compiled defaults.
        if (!api.IsFile(ConfigPath))
            return cfg;

        try
        {
            string content = api.ReadText(ConfigPath);
            foreach (var rawLine in content.Split('\n'))
            {
                string line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith('#')) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim().ToLowerInvariant();
                string val = line[(eq + 1)..].Trim();

                switch (key)
                {
                    case "port":
                        if (int.TryParse(val, out int p) && p > 0 && p <= 65535) cfg.Port = p;
                        break;
                    case "terminal_width":
                        if (int.TryParse(val, out int tw) && tw >= 40 && tw <= 500) cfg.TerminalWidth = tw;
                        break;
                    case "terminal_height":
                        if (int.TryParse(val, out int th) && th >= 10 && th <= 200) cfg.TerminalHeight = th;
                        break;
                    case "max_sessions":
                        if (int.TryParse(val, out int ms) && ms >= 0) cfg.MaxSessions = ms;
                        break;
                    case "max_login_attempts":
                        if (int.TryParse(val, out int mla) && mla >= 1) cfg.MaxLoginAttempts = mla;
                        break;
                    case "idle_timeout":
                        if (int.TryParse(val, out int it) && it >= 0) cfg.IdleTimeoutMinutes = it;
                        break;
                    case "login_banner":
                        cfg.LoginBanner = val;
                        break;
                    case "welcome_message":
                        cfg.WelcomeMessage = val;
                        break;
                    case "log_events":
                        cfg.LogEvents = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "log_sessions":
                        cfg.LogSessions = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "host_log_events":
                        cfg.HostLogEvents = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "host_log_sessions":
                        cfg.HostLogSessions = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }
        }
        catch
        {
            // Use defaults on parse failure
        }

        return cfg;
    }

    // keep config accessible to session threads
    private static TelnetConfig _config = new();

    /// <summary>
    /// Daemon entry point — runs on a background thread.
    /// </summary>
    public static int Daemon(NixApi api, string[] args)
    {
        _config = LoadConfig(api);

        // Command-line port overrides config
        int port = _config.Port;
        if (args.Length > 0 && int.TryParse(args[0], out int p))
            port = p;

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"telnetd: failed to start on port {port}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"telnetd: listening on port {port} (term {_config.TerminalWidth}x{_config.TerminalHeight}, max {(_config.MaxSessions == 0 ? "unlimited" : _config.MaxSessions)} sessions)");

        // Log to VFS
        LogMessage(api, $"telnetd started on port {port}");

        var token = api.DaemonToken;
        int sessionCounter = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                // Poll for connections with cancellation support
                if (!listener.Pending())
                {
                    try { token.WaitHandle.WaitOne(200); } catch { }
                    if (token.IsCancellationRequested) break;
                    continue;
                }

                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    break;
                }

                // Enforce max sessions
                if (_config.MaxSessions > 0)
                {
                    int active;
                    lock (_sessionsLock) active = _sessions.Count;
                    if (active >= _config.MaxSessions)
                    {
                        try
                        {
                            var rejectStream = client.GetStream();
                            byte[] msg = Encoding.UTF8.GetBytes("telnetd: maximum sessions reached. Try again later.\r\n");
                            rejectStream.Write(msg, 0, msg.Length);
                            client.Close();
                        }
                        catch { }
                        continue;
                    }
                }

                int sessionId = Interlocked.Increment(ref sessionCounter);
                string remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

                Console.WriteLine($"telnetd: connection from {remote} (session #{sessionId})");
                LogMessage(api, $"connection from {remote} (session #{sessionId})");

                // Each connection gets its own thread
                var thread = new Thread(() => HandleSession(api, client, sessionId, remote))
                {
                    Name = $"telnet-session-{sessionId}",
                    IsBackground = true,
                };
                thread.Start();
            }
        }
        finally
        {
            try { listener.Stop(); } catch { }
            LogMessage(api, "telnetd stopped");
        }

        return 0;
    }

    private static void HandleSession(NixApi api, TcpClient client, int sessionId, string remote)
    {
        var session = new SessionInfo
        {
            Id = sessionId,
            RemoteEndpoint = remote,
            ConnectedAt = DateTime.Now,
        };

        lock (_sessionsLock) _sessions.Add(session);

        try
        {
            client.NoDelay = true;

            // Apply idle timeout from config
            if (_config.IdleTimeoutMinutes > 0)
            {
                int timeoutMs = _config.IdleTimeoutMinutes * 60 * 1000;
                client.ReceiveTimeout = timeoutMs;
            }
            else
            {
                client.ReceiveTimeout = 0; // no timeout
            }

            var stream = client.GetStream();

            // Send Telnet negotiation: character-at-a-time mode with server echo
            // IAC WILL SUPPRESS-GO-AHEAD
            stream.Write(new byte[] { 255, 251, 3 });
            // IAC WILL ECHO (server will echo)
            stream.Write(new byte[] { 255, 251, 1 });
            // IAC DO SUPPRESS-GO-AHEAD
            stream.Write(new byte[] { 255, 253, 3 });
            // IAC DONT LINEMODE — force character-at-a-time mode
            stream.Write(new byte[] { 255, 254, 34 });
            // IAC DO NAWS — request client to negotiate window size
            stream.Write(new byte[] { 255, 253, 31 });
            stream.Flush();

            var telnetReader = new TelnetLineReader(stream);
            var telnetWriter = new TelnetWriter(stream);
            var rawKeyReader = new TelnetRawKeyReader(stream);

            // Wire up NAWS — when the client sends its window size, update
            // the session's terminal dimensions dynamically.
            Action<int, int> onResize = (w, h) => NetNIX.Shell.SessionIO.SetTerminalSize(w, h);
            telnetReader.OnWindowSize = onResize;
            rawKeyReader.OnWindowSize = onResize;

            // Install per-thread I/O (config values are the initial defaults;
            // the client's NAWS response will override them automatically)
            NetNIX.Shell.SessionIO.Enter(telnetReader, telnetWriter, isRemote: true,
                passwordModeCallback: (enabled) => telnetReader.EchoEnabled = !enabled,
                readKeyCallback: (intercept) => rawKeyReader.ReadKey(intercept, telnetWriter),
                terminalWidth: _config.TerminalWidth,
                terminalHeight: _config.TerminalHeight);

            try
            {
                RunLoginLoop(api, session, telnetReader, telnetWriter, rawKeyReader);
            }
            finally
            {
                NetNIX.Shell.SessionIO.Leave();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // Client disconnected — normal
        }
        catch (Exception)
        {
            // Catch-all: session threads must NEVER crash the host process.
        }
        finally
        {
            lock (_sessionsLock) _sessions.Remove(session);
            try { client.Close(); } catch { }
            LogMessage(api, $"session #{sessionId} from {remote} disconnected");
        }
    }

    private static void RunLoginLoop(NixApi api, SessionInfo session, TelnetLineReader reader, TelnetWriter writer, TelnetRawKeyReader rawKeyReader)
    {
        // Display login banner from config
        string banner = _config.LoginBanner;
        if (banner.Length > 0)
        {
            int boxWidth = Math.Max(banner.Length + 6, 60);
            string inner = $"  {banner}  ";
            int pad = boxWidth - 2 - inner.Length;
            if (pad < 0) pad = 0;

            string connectedLine = "  Connected via Telnet daemon";
            string webLine = "  Web:    netnix.controlfeed.info";
            string sourceLine = "  Source: github.com/squiblez/NetNIX";

            writer.WriteLine("");
            writer.WriteLine("?" + new string('?', boxWidth - 2) + "?");
            writer.WriteLine("?" + inner + new string(' ', pad) + "?");
            writer.WriteLine("?" + connectedLine + new string(' ', boxWidth - 2 - connectedLine.Length) + "?");
            writer.WriteLine("?" + new string(' ', boxWidth - 2) + "?");
            writer.WriteLine("?" + webLine + new string(' ', boxWidth - 2 - webLine.Length) + "?");
            writer.WriteLine("?" + sourceLine + new string(' ', boxWidth - 2 - sourceLine.Length) + "?");
            writer.WriteLine("?" + new string('?', boxWidth - 2) + "?");
            writer.WriteLine("");
        }

        int maxAttempts = _config.MaxLoginAttempts;
        int attempts = 0;

        while (attempts < maxAttempts)
        {
            writer.Write("login: ");
            writer.Flush();
            string? username = reader.ReadLine();
            if (username == null) return; // disconnected

            username = username.Trim();
            if (username.Length == 0) continue;

            writer.Write("password: ");
            writer.Flush();

            // Disable echo for password
            reader.EchoEnabled = false;
            string? password = reader.ReadLine();
            reader.EchoEnabled = true;
            writer.WriteLine(""); // newline after hidden password

            if (password == null) return; // disconnected

            // Authenticate via the api's user management
            var user = api.AuthenticateUser(username, password.Trim());
            if (user == null)
            {
                writer.WriteLine("Login incorrect.");
                writer.WriteLine("");
                attempts++;
                LogMessage(api, $"failed login for '{username}' from {session.RemoteEndpoint}");
                continue;
            }

            session.Username = username;
            LogMessage(api, $"user '{username}' logged in from {session.RemoteEndpoint}");
            LogSession(api, username, $"session #{session.Id} started from {session.RemoteEndpoint}");

            // Clear the screen and show welcome message
            writer.Write("\x1b[2J\x1b[H\x1b[3J");
            string welcome = _config.WelcomeMessage
                .Replace("{user}", username)
                .Replace("{session}", session.Id.ToString());
            writer.WriteLine(welcome);
            writer.WriteLine("");

            // If session logging is enabled, wrap the I/O to capture activity
            bool sessionLogging = _config.LogSessions || _config.HostLogSessions;
            if (sessionLogging)
            {
                var logWriter = new LoggingWriter(writer, api, username, session.Id);
                var logReader = new LoggingReader(reader, api, username, session.Id);
                NetNIX.Shell.SessionIO.Enter(logReader, logWriter, isRemote: true,
                    passwordModeCallback: (enabled) => reader.EchoEnabled = !enabled,
                    readKeyCallback: (intercept) => rawKeyReader.ReadKey(intercept, writer),
                    terminalWidth: NetNIX.Shell.SessionIO.WindowWidth,
                    terminalHeight: NetNIX.Shell.SessionIO.WindowHeight);
            }

            // Create and run a shell for this session
            var scriptRunner = api.GetScriptRunner();
            var daemonMgr = api.GetDaemonManager();
            if (scriptRunner == null || daemonMgr == null)
            {
                writer.WriteLine("telnetd: internal error — runtime not initialised");
                return;
            }

            var shell = new NetNIX.Shell.NixShell(
                api.GetFileSystem(),
                api.GetUserManager(),
                scriptRunner,
                daemonMgr,
                user,
                isRemote: true
            );

            try
            {
                shell.Run();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return; // disconnected
            }
            catch (Exception ex)
            {
                writer.WriteLine($"nsh: fatal: {ex.GetType().Name}: {ex.Message}");
            }

            LogMessage(api, $"user '{username}' logged out (session #{session.Id})");
            LogSession(api, username, $"session #{session.Id} ended");
            session.Username = "(login)";
            writer.WriteLine($"{username} logged out.");
            writer.WriteLine("");
            attempts = 0; // reset after successful session
        }

        writer.WriteLine("Too many failed login attempts. Disconnecting.");
    }

    // ?? Logging ????????????????????????????????????????????????????

    /// <summary>
    /// Log a daemon event (connections, logins, disconnections, etc.)
    /// to VFS (/var/log/telnetd.log) and/or host (logs/telnetd.log)
    /// depending on configuration.
    /// </summary>
    private static void LogMessage(NixApi api, string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string logLine = $"[{timestamp}] {message}\n";

        if (_config.LogEvents)
        {
            try
            {
                bool isNew = !api.IsFile("/var/log/telnetd.log");
                api.AppendText("/var/log/telnetd.log", logLine);
                if (isNew)
                    api.Chmod("/var/log/telnetd.log", "rw-------");
                api.Save();
            }
            catch { }
        }

        if (_config.HostLogEvents)
        {
            try { api.HostLog("telnetd", message); } catch { }
        }
    }

    /// <summary>
    /// Log a per-user session activity line (commands typed, output, etc.)
    /// to VFS (/var/log/telnetd/{user}.log) and/or host (logs/telnetd-{user}.log).
    /// </summary>
    private static void LogSession(NixApi api, string username, string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string logLine = $"[{timestamp}] {message}\n";

        if (_config.LogSessions)
        {
            try
            {
                // Ensure per-user log directory exists with root-only access
                if (!api.IsDirectory("/var/log/telnetd"))
                {
                    api.CreateDir("/var/log/telnetd");
                    api.Chmod("/var/log/telnetd", "rwx------");
                }
                string logPath = $"/var/log/telnetd/{username}.log";
                bool isNew = !api.IsFile(logPath);
                api.AppendText(logPath, logLine);
                if (isNew)
                    api.Chmod(logPath, "rw-------");
                api.Save();
            }
            catch { }
        }

        if (_config.HostLogSessions)
        {
            try { api.HostLog($"telnetd-{username}", message); } catch { }
        }
    }

    /// <summary>
    /// Wraps a TelnetWriter to capture all output for session logging.
    /// </summary>
    private class LoggingWriter : TextWriter
    {
        private readonly TelnetWriter _inner;
        private readonly NixApi _api;
        private readonly string _username;
        private readonly int _sessionId;
        public override Encoding Encoding => Encoding.UTF8;

        public LoggingWriter(TelnetWriter inner, NixApi api, string username, int sessionId)
        {
            _inner = inner;
            _api = api;
            _username = username;
            _sessionId = sessionId;
        }

        public override void Write(char value)
        {
            _inner.Write(value);
        }

        public override void Write(string? value)
        {
            _inner.Write(value);
        }

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);
            // Log the output line (strip ANSI escapes for readability)
            if (value != null)
                LogSession(_api, _username, $"[S#{_sessionId} OUT] {StripAnsi(value)}");
        }

        public override void WriteLine()
        {
            _inner.WriteLine();
        }

        public override void Flush() => _inner.Flush();

        private static string StripAnsi(string s)
        {
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '\x1b' && i + 1 < s.Length && s[i + 1] == '[')
                {
                    i += 2;
                    while (i < s.Length && !char.IsLetter(s[i]) && s[i] != '~') i++;
                    if (i < s.Length) i++;
                }
                else
                {
                    sb.Append(s[i]);
                    i++;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Wraps a TelnetLineReader to capture all user input for session logging.
    /// </summary>
    private class LoggingReader : TextReader
    {
        private readonly TelnetLineReader _inner;
        private readonly NixApi _api;
        private readonly string _username;
        private readonly int _sessionId;

        public LoggingReader(TelnetLineReader inner, NixApi api, string username, int sessionId)
        {
            _inner = inner;
            _api = api;
            _username = username;
            _sessionId = sessionId;
        }

        public override string? ReadLine()
        {
            string? line = _inner.ReadLine();
            if (line != null)
                LogSession(_api, _username, $"[S#{_sessionId} IN ] {line}");
            return line;
        }

        public override int Read() => _inner.Read();
        public override int Peek() => _inner.Peek();
    }

    /// <summary>
    /// Telnet-aware line reader that strips IAC sequences, handles
    /// backspace, and provides line-at-a-time input with optional echo.
    /// </summary>
    private class TelnetLineReader : TextReader
    {
        private readonly NetworkStream _stream;
        public bool EchoEnabled { get; set; } = true;

        /// <summary>
        /// Callback invoked when a NAWS (window size) subnegotiation is
        /// received from the client. Parameters are (width, height).
        /// </summary>
        public Action<int, int>? OnWindowSize { get; set; }

        public TelnetLineReader(NetworkStream stream) => _stream = stream;

        public override string? ReadLine()
        {
            var sb = new StringBuilder();
            try
            {
                while (true)
                {
                    int b = _stream.ReadByte();
                    if (b < 0) return sb.Length > 0 ? sb.ToString() : null; // EOF

                    // Handle Telnet IAC sequences
                    if (b == 255) // IAC
                    {
                        int cmd = _stream.ReadByte();
                        if (cmd < 0) return sb.Length > 0 ? sb.ToString() : null;

                        if (cmd >= 251 && cmd <= 254) // WILL/WONT/DO/DONT
                        {
                            _stream.ReadByte(); // skip option byte
                            continue;
                        }
                        if (cmd == 250) // SB (subnegotiation)
                        {
                            ParseSubnegotiation();
                            continue;
                        }
                        if (cmd == 255) // escaped 0xFF data byte
                        {
                            sb.Append((char)255);
                            continue;
                        }
                        continue; // skip other IAC commands
                    }

                    // CR LF or CR NUL or just LF = end of line
                    if (b == '\r')
                    {
                        // Peek at next byte — if LF or NUL, consume it
                        if (_stream.DataAvailable)
                        {
                            int next = _stream.ReadByte();
                            if (next >= 0 && next != '\n' && next != 0)
                            {
                                // Not LF or NUL — treat as part of input
                                sb.Append((char)next);
                            }
                        }
                        if (EchoEnabled) EchoBytes(new byte[] { (byte)'\r', (byte)'\n' });
                        return sb.ToString();
                    }
                    if (b == '\n')
                    {
                        if (EchoEnabled) EchoBytes(new byte[] { (byte)'\r', (byte)'\n' });
                        return sb.ToString();
                    }

                    // Backspace / DEL
                    if (b == 8 || b == 127)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Remove(sb.Length - 1, 1);
                            if (EchoEnabled) EchoBytes(new byte[] { 8, (byte)' ', 8 }); // BS, space, BS
                        }
                        continue;
                    }

                    // Regular printable character
                    if (b >= 32)
                    {
                        sb.Append((char)b);
                        if (EchoEnabled)
                            EchoBytes(new byte[] { (byte)b });
                        else
                            EchoBytes(new byte[] { (byte)'*' }); // mask for password
                    }
                }
            }
            catch (Exception) when (!_stream.CanRead)
            {
                return sb.Length > 0 ? sb.ToString() : null;
            }
        }

        private void EchoBytes(byte[] data)
        {
            try { _stream.Write(data, 0, data.Length); } catch { }
        }

        /// <summary>
        /// Parse a Telnet subnegotiation sequence (after IAC SB has been read).
        /// Handles NAWS (option 31) to capture client window size.
        /// All other subnegotiations are skipped.
        /// </summary>
        private void ParseSubnegotiation()
        {
            int option = _stream.ReadByte();
            if (option < 0) return;

            if (option == 31) // NAWS — Negotiate About Window Size
            {
                // Format: <width-hi> <width-lo> <height-hi> <height-lo> IAC SE
                int wh = _stream.ReadByte();
                int wl = _stream.ReadByte();
                int hh = _stream.ReadByte();
                int hl = _stream.ReadByte();
                // Consume IAC SE
                _stream.ReadByte(); // 255
                _stream.ReadByte(); // 240

                if (wh >= 0 && wl >= 0 && hh >= 0 && hl >= 0)
                {
                    int width = (wh << 8) | wl;
                    int height = (hh << 8) | hl;
                    if (width > 0 && height > 0)
                        OnWindowSize?.Invoke(width, height);
                }
                return;
            }

            // Skip unknown subnegotiations until IAC SE
            while (true)
            {
                int s = _stream.ReadByte();
                if (s < 0) return;
                if (s == 255)
                {
                    int se = _stream.ReadByte();
                    if (se == 240) break; // SE
                }
            }
        }

        public override int Read()
        {
            try
            {
                while (true)
                {
                    int b = _stream.ReadByte();
                    if (b < 0) return -1;
                    // Skip IAC sequences
                    if (b == 255)
                    {
                        int cmd = _stream.ReadByte();
                        if (cmd < 0) return -1;
                        if (cmd >= 251 && cmd <= 254) { _stream.ReadByte(); continue; }
                        if (cmd == 250) { ParseSubnegotiation(); continue; }
                        if (cmd == 255) return 255;
                        continue;
                    }
                    return b;
                }
            }
            catch { return -1; }
        }
    }

    /// <summary>
    /// Telnet-aware writer that converts \n to \r\n for Telnet protocol.
    /// </summary>
    private class TelnetWriter : TextWriter
    {
        private readonly NetworkStream _stream;
        public override Encoding Encoding => Encoding.UTF8;

        public TelnetWriter(NetworkStream stream) => _stream = stream;

        public override void Write(char value)
        {
            try
            {
                if (value == '\n')
                    _stream.Write(new byte[] { (byte)'\r', (byte)'\n' });
                else
                    _stream.WriteByte((byte)value);
                _stream.Flush();
            }
            catch { /* disconnected */ }
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            try
            {
                // Convert LF to CRLF for Telnet
                var bytes = Encoding.GetBytes(value.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
            }
            catch { /* disconnected */ }
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write("\r\n");
        }

        public override void WriteLine()
        {
            Write("\r\n");
        }

        public override void Flush()
        {
            try { _stream.Flush(); } catch { }
        }
    }

    /// <summary>
    /// Reads raw bytes from the Telnet stream and translates them into
    /// ConsoleKeyInfo, handling ANSI escape sequences for arrow keys,
    /// function keys, Home, End, PageUp/Down, Delete, etc.
    /// </summary>
    private class TelnetRawKeyReader
    {
        private readonly NetworkStream _stream;
        public Action<int, int>? OnWindowSize { get; set; }

        public TelnetRawKeyReader(NetworkStream stream) => _stream = stream;

        /// <summary>
        /// Read a single keypress from the Telnet stream.
        /// Translates ANSI escape sequences to ConsoleKeyInfo.
        /// </summary>
        public ConsoleKeyInfo ReadKey(bool intercept, TelnetWriter writer)
        {
            while (true)
            {
                int b = ReadByte();
                if (b < 0) throw new IOException("Connection closed");

                // Telnet IAC — skip negotiation
                if (b == 255)
                {
                    int cmd = ReadByte();
                    if (cmd >= 251 && cmd <= 254) ReadByte(); // WILL/WONT/DO/DONT + option
                    else if (cmd == 250) ParseSubnegotiation();
                    continue;
                }

                // ESC — start of ANSI escape sequence
                if (b == 27)
                {
                    return ParseEscapeSequence(intercept, writer);
                }

                // Ctrl+key (0x01-0x1A = Ctrl+A through Ctrl+Z)
                if (b >= 1 && b <= 26)
                {
                    char ctrlChar = (char)b;
                    ConsoleKey ck = (ConsoleKey)('A' + b - 1);
                    // Special cases
                    if (b == 13) return MakeKey('\r', ConsoleKey.Enter, false, false, false);
                    if (b == 10) return MakeKey('\n', ConsoleKey.Enter, false, false, false);
                    if (b == 9) return MakeKey('\t', ConsoleKey.Tab, false, false, false);
                    if (b == 8) return MakeKey('\b', ConsoleKey.Backspace, false, false, false);
                    return MakeKey(ctrlChar, ck, false, false, true);
                }

                // CR
                if (b == 13)
                {
                    // Consume LF or NUL after CR
                    if (_stream.DataAvailable)
                    {
                        int next = ReadByte();
                        if (next >= 0 && next != 10 && next != 0)
                        {
                            // Not LF or NUL — we'll lose this byte, but it's rare
                        }
                    }
                    return MakeKey('\r', ConsoleKey.Enter, false, false, false);
                }

                // Backspace / DEL
                if (b == 127) return MakeKey('\b', ConsoleKey.Backspace, false, false, false);

                // Regular printable character
                char ch = (char)b;
                if (!intercept) writer.Write(ch);
                ConsoleKey key = CharToConsoleKey(ch);
                bool shift = char.IsUpper(ch);
                return MakeKey(ch, key, shift, false, false);
            }
        }

        private ConsoleKeyInfo ParseEscapeSequence(bool intercept, TelnetWriter writer)
        {
            // Wait briefly for more bytes (some terminals send bare ESC)
            Thread.Sleep(20);
            if (!_stream.DataAvailable)
                return MakeKey((char)27, ConsoleKey.Escape, false, false, false);

            int b2 = ReadByte();

            // ESC [ = CSI (Control Sequence Introducer)
            if (b2 == '[')
            {
                // Read the full sequence: digits and semicolons, ending with a letter or ~
                var seq = new StringBuilder();
                while (true)
                {
                    int s = ReadByte();
                    if (s < 0) break;
                    char c = (char)s;
                    seq.Append(c);
                    if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '~')
                        break;
                }

                string code = seq.ToString();
                return MapCSI(code);
            }

            // ESC O = SS3 (function keys on some terminals)
            if (b2 == 'O')
            {
                int b3 = ReadByte();
                if (b3 < 0) return MakeKey((char)27, ConsoleKey.Escape, false, false, false);
                return (char)b3 switch
                {
                    'P' => MakeKey('\0', ConsoleKey.F1, false, false, false),
                    'Q' => MakeKey('\0', ConsoleKey.F2, false, false, false),
                    'R' => MakeKey('\0', ConsoleKey.F3, false, false, false),
                    'S' => MakeKey('\0', ConsoleKey.F4, false, false, false),
                    'H' => MakeKey('\0', ConsoleKey.Home, false, false, false),
                    'F' => MakeKey('\0', ConsoleKey.End, false, false, false),
                    _ => MakeKey((char)27, ConsoleKey.Escape, false, false, false),
                };
            }

            // Alt+key
            if (b2 >= 32)
            {
                char ch = (char)b2;
                ConsoleKey ck = CharToConsoleKey(ch);
                return MakeKey(ch, ck, false, true, false);
            }

            return MakeKey((char)27, ConsoleKey.Escape, false, false, false);
        }

        private static ConsoleKeyInfo MapCSI(string code)
        {
            // Arrow keys
            if (code == "A") return MakeKey('\0', ConsoleKey.UpArrow, false, false, false);
            if (code == "B") return MakeKey('\0', ConsoleKey.DownArrow, false, false, false);
            if (code == "C") return MakeKey('\0', ConsoleKey.RightArrow, false, false, false);
            if (code == "D") return MakeKey('\0', ConsoleKey.LeftArrow, false, false, false);
            if (code == "H") return MakeKey('\0', ConsoleKey.Home, false, false, false);
            if (code == "F") return MakeKey('\0', ConsoleKey.End, false, false, false);

            // Numbered sequences (ESC [ N ~)
            if (code.EndsWith("~"))
            {
                string num = code.TrimEnd('~').Split(';')[0]; // ignore modifiers for now
                bool shift = code.Contains(";2");
                bool alt = code.Contains(";3");
                bool ctrl = code.Contains(";5");
                return num switch
                {
                    "1" => MakeKey('\0', ConsoleKey.Home, shift, alt, ctrl),
                    "2" => MakeKey('\0', ConsoleKey.Insert, shift, alt, ctrl),
                    "3" => MakeKey('\0', ConsoleKey.Delete, shift, alt, ctrl),
                    "4" => MakeKey('\0', ConsoleKey.End, shift, alt, ctrl),
                    "5" => MakeKey('\0', ConsoleKey.PageUp, shift, alt, ctrl),
                    "6" => MakeKey('\0', ConsoleKey.PageDown, shift, alt, ctrl),
                    "11" => MakeKey('\0', ConsoleKey.F1, shift, alt, ctrl),
                    "12" => MakeKey('\0', ConsoleKey.F2, shift, alt, ctrl),
                    "13" => MakeKey('\0', ConsoleKey.F3, shift, alt, ctrl),
                    "14" => MakeKey('\0', ConsoleKey.F4, shift, alt, ctrl),
                    "15" => MakeKey('\0', ConsoleKey.F5, shift, alt, ctrl),
                    "17" => MakeKey('\0', ConsoleKey.F6, shift, alt, ctrl),
                    "18" => MakeKey('\0', ConsoleKey.F7, shift, alt, ctrl),
                    "19" => MakeKey('\0', ConsoleKey.F8, shift, alt, ctrl),
                    "20" => MakeKey('\0', ConsoleKey.F9, shift, alt, ctrl),
                    "21" => MakeKey('\0', ConsoleKey.F10, shift, alt, ctrl),
                    "23" => MakeKey('\0', ConsoleKey.F11, shift, alt, ctrl),
                    "24" => MakeKey('\0', ConsoleKey.F12, shift, alt, ctrl),
                    _ => MakeKey('\0', ConsoleKey.NoName, false, false, false),
                };
            }

            // Modified arrow keys: ESC [ 1 ; modifier letter
            if (code.Length >= 3 && code[0] == '1' && code[1] == ';')
            {
                char letter = code[^1];
                int mod = 0;
                if (code.Length > 2) int.TryParse(code.Substring(2, code.Length - 3), out mod);
                bool shift = mod == 2 || mod == 6;
                bool alt = mod == 3 || mod == 7;
                bool ctrl = mod == 5 || mod == 6 || mod == 7;
                return letter switch
                {
                    'A' => MakeKey('\0', ConsoleKey.UpArrow, shift, alt, ctrl),
                    'B' => MakeKey('\0', ConsoleKey.DownArrow, shift, alt, ctrl),
                    'C' => MakeKey('\0', ConsoleKey.RightArrow, shift, alt, ctrl),
                    'D' => MakeKey('\0', ConsoleKey.LeftArrow, shift, alt, ctrl),
                    'H' => MakeKey('\0', ConsoleKey.Home, shift, alt, ctrl),
                    'F' => MakeKey('\0', ConsoleKey.End, shift, alt, ctrl),
                    _ => MakeKey('\0', ConsoleKey.NoName, false, false, false),
                };
            }

            return MakeKey('\0', ConsoleKey.NoName, false, false, false);
        }

        private int ReadByte()
        {
            try { return _stream.ReadByte(); }
            catch { return -1; }
        }

        private void ParseSubnegotiation()
        {
            int option = ReadByte();
            if (option < 0) return;

            if (option == 31) // NAWS
            {
                int wh = ReadByte(), wl = ReadByte();
                int hh = ReadByte(), hl = ReadByte();
                ReadByte(); // IAC (255)
                ReadByte(); // SE  (240)
                if (wh >= 0 && wl >= 0 && hh >= 0 && hl >= 0)
                {
                    int w = (wh << 8) | wl;
                    int h = (hh << 8) | hl;
                    if (w > 0 && h > 0) OnWindowSize?.Invoke(w, h);
                }
                return;
            }

            // Skip unknown subnegotiation until IAC SE
            while (true)
            {
                int s = ReadByte();
                if (s < 0) return;
                if (s == 255) { if (ReadByte() == 240) break; }
            }
        }

        private static ConsoleKeyInfo MakeKey(char keyChar, ConsoleKey key, bool shift, bool alt, bool control)
        {
            return new ConsoleKeyInfo(keyChar, key, shift, alt, control);
        }

        private static ConsoleKey CharToConsoleKey(char c)
        {
            c = char.ToUpper(c);
            if (c >= 'A' && c <= 'Z') return (ConsoleKey)c;
            if (c >= '0' && c <= '9') return (ConsoleKey)c;
            return c switch
            {
                ' ' => ConsoleKey.Spacebar,
                '\t' => ConsoleKey.Tab,
                _ => ConsoleKey.NoName,
            };
        }
    }
}
