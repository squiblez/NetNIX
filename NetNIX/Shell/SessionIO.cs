/*
Copyright (C) 2026 Michael Sullender
This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
You should have received a copy of the GNU General Public License along with this program. If not, see gnu.org
*/
using System.Text;

namespace NetNIX.Shell;

/// <summary>
/// Thread-aware I/O multiplexer that allows multiple concurrent sessions
/// (local console + Telnet connections) to share the process while each
/// sees its own stdin/stdout through Console.In/Console.Out.
///
/// Each session thread calls <see cref="Enter"/> with its own TextReader
/// and TextWriter before running the shell, and <see cref="Leave"/> when
/// the session ends. Any Console.Write/ReadLine calls on that thread are
/// automatically routed to the correct session's streams.
/// </summary>
public static class SessionIO
{
    private static readonly AsyncLocal<TextWriter?> _threadOut = new();
    private static readonly AsyncLocal<TextReader?> _threadIn = new();
    private static readonly AsyncLocal<bool> _isRemote = new();
    private static readonly AsyncLocal<Action<bool>?> _passwordModeCallback = new();
    private static readonly AsyncLocal<Func<bool, ConsoleKeyInfo>?> _readKeyCallback = new();
    private static readonly AsyncLocal<int> _termWidth = new();
    private static readonly AsyncLocal<int> _termHeight = new();

    private static TextWriter? _originalOut;
    private static TextReader? _originalIn;
    private static bool _installed;
    private static readonly object _installLock = new();

    /// <summary>
    /// Install the thread-aware Console wrappers. Call once at startup.
    /// Safe to call multiple times (idempotent).
    ///
    /// After Install(), Console.Out and Console.In automatically route
    /// to the AsyncLocal session writers/readers configured by Enter()
    /// on each thread. This means ANY Console.Write/ReadLine call - from
    /// builtins, scripts, or library code - lands in the correct
    /// session's stream without any per-call SetOut/SetIn dance.
    ///
    /// .NET's Console wraps our writer in a SyncTextWriter that holds
    /// a lock per-Write call. That lock is brief (single character or
    /// string write) and is the same locking pattern already used by
    /// any code that touches Console concurrently - it is not held
    /// across script execution and does NOT serialize sessions.
    /// </summary>
    public static void Install()
    {
        lock (_installLock)
        {
            if (_installed) return;
            _originalOut = Console.Out;
            _originalIn = Console.In;
            // Install the thread-aware wrappers globally. From now on
            // every Console.Write call routes through AsyncLocal to
            // whichever session writer the calling thread set up via
            // Enter(), or falls back to the original host console for
            // threads that never entered a session (e.g. the local
            // login loop in Program.Main).
            Console.SetOut(new ThreadAwareWriter());
            Console.SetIn(new ThreadAwareReader());
            _installed = true;
        }
    }

    /// <summary>
    /// Historically this temporarily called Console.SetOut to point
    /// the global Console.Out at the current session's writer, then
    /// restored on Dispose. That mutated process-wide state and raced
    /// across concurrent sessions - the agent and the local user could
    /// see each other's script output.
    ///
    /// After Install() now wires a ThreadAwareWriter as Console.Out
    /// directly, every Console.Write already routes per-thread via
    /// AsyncLocal. So this method is now a no-op kept only for source
    /// compatibility with existing call sites (notably ScriptRunner).
    /// </summary>
    public static IDisposable RedirectConsoleForScript() => _noopRestorer;

    private static readonly IDisposable _noopRestorer = new NoopRestorer();

    private sealed class NoopRestorer : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>
    /// Mark the current thread/async-context as a session with its own I/O.
    /// </summary>
    /// <param name="passwordModeCallback">
    /// Optional callback invoked with true to enter password mode (mask input)
    /// and false to leave password mode. Used by Telnet sessions.
    /// </param>
    /// <param name="readKeyCallback">
    /// Optional callback that reads a single keypress from the remote stream.
    /// The bool parameter is the intercept flag (true = don't echo).
    /// </param>
    public static void Enter(TextReader input, TextWriter output, bool isRemote = false,
        Action<bool>? passwordModeCallback = null, Func<bool, ConsoleKeyInfo>? readKeyCallback = null,
        int terminalWidth = 0, int terminalHeight = 0)
    {
        _threadOut.Value = output;
        _threadIn.Value = input;
        _isRemote.Value = isRemote;
        _passwordModeCallback.Value = passwordModeCallback;
        _readKeyCallback.Value = readKeyCallback;
        _termWidth.Value = terminalWidth;
        _termHeight.Value = terminalHeight;
    }

    /// <summary>
    /// Remove the current thread's session I/O, reverting to the default console.
    /// </summary>
    public static void Leave()
    {
        _threadOut.Value = null;
        _threadIn.Value = null;
        _isRemote.Value = false;
        _passwordModeCallback.Value = null;
        _readKeyCallback.Value = null;
        _termWidth.Value = 0;
        _termHeight.Value = 0;
    }

    /// <summary>
    /// Enter or leave password mode for the current session.
    /// When in password mode, input is masked (e.g. with '*' characters).
    /// No-op for local console sessions.
    /// </summary>
    public static void SetPasswordMode(bool enabled)
    {
        _passwordModeCallback.Value?.Invoke(enabled);
    }

    /// <summary>
    /// Read a single keypress. For remote sessions this reads from the
    /// network stream and translates ANSI escape sequences into
    /// <see cref="ConsoleKeyInfo"/>. For local sessions this delegates
    /// to <see cref="Console.ReadKey(bool)"/>.
    ///
    /// Scripts should call this instead of Console.ReadKey so that
    /// interactive commands (editors, pagers, etc.) work over Telnet.
    ///
    /// On remote disconnect this returns a sentinel Escape key rather than
    /// throwing — ensuring SessionIO never crashes the host process.
    /// </summary>
    public static ConsoleKeyInfo ReadKey(bool intercept = false)
    {
        var cb = _readKeyCallback.Value;
        if (cb != null)
        {
            try
            {
                return cb(intercept);
            }
            catch
            {
                // Remote client disconnected — return Escape so callers
                // exit their input loops cleanly.
                return new ConsoleKeyInfo((char)27, ConsoleKey.Escape, false, false, false);
            }
        }

        // Orphaned remote thread — return Escape
        if (_isRemote.Value)
            return new ConsoleKeyInfo((char)27, ConsoleKey.Escape, false, false, false);

        return Console.ReadKey(intercept);
    }

    /// <summary>
    /// The effective TextWriter for the current thread.
    /// For remote sessions returns a <see cref="SafeRemoteWriter"/> that
    /// silently swallows exceptions on write — preventing cascading crashes
    /// when a remote client disconnects mid-operation.
    /// </summary>
    public static TextWriter Out
    {
        get
        {
            var w = _threadOut.Value;
            if (w != null)
                return _isRemote.Value ? new SafeRemoteWriter(w) : w;
            return _originalOut ?? Console.Out;
        }
    }

    /// <summary>
    /// The effective TextReader for the current thread.
    /// For remote sessions returns a <see cref="SafeRemoteReader"/> that
    /// returns null/EOF on exceptions instead of throwing.
    /// </summary>
    public static TextReader In
    {
        get
        {
            var r = _threadIn.Value;
            if (r != null)
                return _isRemote.Value ? new SafeRemoteReader(r) : r;
            return _originalIn ?? Console.In;
        }
    }

    /// <summary>
    /// Whether the current thread is a remote (Telnet) session.
    /// Used to disable Console-specific APIs (ReadKey, Clear, etc.).
    /// </summary>
    public static bool IsRemote => _isRemote.Value;

    /// <summary>
    /// Terminal width for the current session. For remote sessions this
    /// comes from the telnetd configuration. For local sessions it
    /// falls back to <see cref="Console.WindowWidth"/>.
    /// </summary>
    public static int WindowWidth
    {
        get
        {
            int w = _termWidth.Value;
            if (w > 0) return w;
            try { return Console.WindowWidth; } catch { return 80; }
        }
    }

    /// <summary>
    /// Terminal height for the current session. For remote sessions this
    /// comes from the telnetd configuration. For local sessions it
    /// falls back to <see cref="Console.WindowHeight"/>.
    /// </summary>
    public static int WindowHeight
    {
        get
        {
            int h = _termHeight.Value;
            if (h > 0) return h;
            try { return Console.WindowHeight; } catch { return 24; }
        }
    }

    /// <summary>
    /// Update the terminal dimensions for the current session.
    /// Called when a Telnet client sends a NAWS (window size) update.
    /// </summary>
    public static void SetTerminalSize(int width, int height)
    {
        if (width > 0) _termWidth.Value = width;
        if (height > 0) _termHeight.Value = height;
    }

    // ?? Thread-aware Console.Out replacement ???????????????????????

    private sealed class ThreadAwareWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        private TextWriter Target => _threadOut.Value ?? _originalOut!;

        public override void Write(char value) => Target.Write(value);
        public override void Write(string? value) => Target.Write(value);
        public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);
        public override void WriteLine() => Target.WriteLine();
        public override void WriteLine(string? value) => Target.WriteLine(value);
        public override void Flush() => Target.Flush();

        public override Task WriteAsync(char value) => Target.WriteAsync(value);
        public override Task WriteAsync(string? value) => Target.WriteAsync(value);
        public override Task WriteLineAsync(string? value) => Target.WriteLineAsync(value);
        public override Task FlushAsync() => Target.FlushAsync();
    }

    // ?? Thread-aware Console.In replacement ????????????????????????

    private sealed class ThreadAwareReader : TextReader
    {
        private TextReader Target => _threadIn.Value ?? _originalIn!;

        public override int Read() => Target.Read();
        public override int Read(char[] buffer, int index, int count) => Target.Read(buffer, index, count);
        public override string? ReadLine() => Target.ReadLine();
        public override int Peek() => Target.Peek();

        public override Task<int> ReadAsync(char[] buffer, int index, int count) => Target.ReadAsync(buffer, index, count);
        public override Task<string?> ReadLineAsync() => Target.ReadLineAsync();
    }

    // ?? Safe writer for remote sessions ????????????????????????????

    /// <summary>
    /// Wraps a remote session's TextWriter so that any write after the
    /// client disconnects is silently swallowed instead of throwing.
    /// This prevents cascading crashes when error-handling code tries
    /// to print messages to a dead connection.
    /// </summary>
    private sealed class SafeRemoteWriter : TextWriter
    {
        private readonly TextWriter _inner;
        public override Encoding Encoding => Encoding.UTF8;

        public SafeRemoteWriter(TextWriter inner) => _inner = inner;

        public override void Write(char value) { try { _inner.Write(value); } catch { } }
        public override void Write(string? value) { try { _inner.Write(value); } catch { } }
        public override void Write(char[] buffer, int index, int count) { try { _inner.Write(buffer, index, count); } catch { } }
        public override void WriteLine() { try { _inner.WriteLine(); } catch { } }
        public override void WriteLine(string? value) { try { _inner.WriteLine(value); } catch { } }
        public override void Flush() { try { _inner.Flush(); } catch { } }
    }

    // ?? Safe reader for remote sessions ????????????????????????????

    /// <summary>
    /// Wraps a remote session's TextReader so that any read after the
    /// client disconnects returns null/EOF instead of throwing.
    /// </summary>
    private sealed class SafeRemoteReader : TextReader
    {
        private readonly TextReader _inner;

        public SafeRemoteReader(TextReader inner) => _inner = inner;

        public override int Read() { try { return _inner.Read(); } catch { return -1; } }
        public override int Read(char[] buffer, int index, int count) { try { return _inner.Read(buffer, index, count); } catch { return 0; } }
        public override string? ReadLine() { try { return _inner.ReadLine(); } catch { return null; } }
        public override int Peek() { try { return _inner.Peek(); } catch { return -1; } }
    }
}
