using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NetNIX.Scripting;

/// <summary>
/// editor (nedit) — A full-screen text editor for NetNIX.
///
/// Supports cursor movement, insert/delete, scrolling, line numbers,
/// auto-indent, cut/paste lines, go-to-line, C# code templates,
/// and save with permission checking.
///
/// Usage:
///     edit &lt;file&gt;
///
/// Keyboard shortcuts:
///     F2 / Ctrl+W     Save
///     Ctrl+Q          Quit (prompts if unsaved)
///     Ctrl+G          Go to line
///     Ctrl+K          Cut current line
///     Ctrl+U          Paste last cut line
///     Ctrl+T          Insert code template
///     Arrow keys      Move cursor
///     Home / End      Start / end of line
///     Page Up/Down    Scroll by page
///     Tab             Insert 4 spaces
///     Enter           New line (with auto-indent)
/// </summary>
public static class EditorCommand
{
    // Editor state
    private static NixApi _api;
    private static string _filePath;
    private static List<StringBuilder> _lines;
    private static int _cursorRow;
    private static int _cursorCol;
    private static int _scrollOffset;
    private static int _scrollX; // horizontal scroll offset
    private static bool _modified;
    private static bool _quit;
    private static string _statusMessage = "";
    private static DateTime _statusTime = DateTime.MinValue;
    private static int _editorRows;
    private static int _editorCols;
    private static List<string> _clipboard = new List<string>();
    private static bool _suppressAutoIndent;

    private const string EditorName = "nedit";
    private const int StatusTimeout = 4;
    private const int GutterWidth = 7; // "1234 | "

    public static int Run(NixApi api, string[] args)
    {
        if (args.Length == 0 || args[0] == "-h" || args[0] == "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        _api = api;
        _filePath = api.ResolvePath(args[0]);
        _lines = new List<StringBuilder>();
        _lines.Add(new StringBuilder());
        _cursorRow = 0;
        _cursorCol = 0;
        _scrollOffset = 0;
        _scrollX = 0;
        _modified = false;
        _quit = false;
        _statusMessage = "";
        _statusTime = DateTime.MinValue;
        _clipboard = new List<string>();
        _suppressAutoIndent = false;

        // Validate path
        if (api.IsDirectory(_filePath))
        {
            Console.WriteLine("edit: " + args[0] + ": Is a directory");
            return 1;
        }

        if (api.IsFile(_filePath))
        {
            if (!api.CanRead(_filePath))
            {
                Console.WriteLine("edit: " + args[0] + ": Permission denied");
                return 1;
            }
            if (!api.CanWrite(_filePath))
            {
                Console.WriteLine("edit: " + args[0] + ": Permission denied (read-only)");
                return 1;
            }
        }
        else
        {
            // New file — check parent write permission
            string parent = api.GetParent(_filePath);
            if (api.IsDirectory(parent) && !api.CanWrite(parent))
            {
                Console.WriteLine("edit: " + args[0] + ": Permission denied");
                return 1;
            }
        }

        // Load file
        LoadFile();

        try { Console.CursorVisible = false; } catch { }
        try
        {
            while (!_quit)
            {
                Render();
                ProcessKey();
            }
        }
        finally
        {
            try { Console.CursorVisible = true; } catch { }
            try { Console.Write("\x1b[2J\x1b[H\x1b[3J"); } catch { } // ANSI clear screen + scrollback
        }

        return 0;
    }

    // ?? File I/O ??????????????????????????????????????????????????

    private static void LoadFile()
    {
        if (_api.IsFile(_filePath))
        {
            string text = _api.ReadText(_filePath);
            var split = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            _lines = new List<StringBuilder>();
            foreach (string s in split)
                _lines.Add(new StringBuilder(s));
            if (_lines.Count == 0) _lines.Add(new StringBuilder());
        }
    }

    private static void SaveFile()
    {
        // Re-check write permission
        if (_api.IsFile(_filePath) && !_api.CanWrite(_filePath))
        {
            SetStatus("Permission denied - cannot save");
            return;
        }
        if (!_api.IsFile(_filePath))
        {
            string parent = _api.GetParent(_filePath);
            if (_api.IsDirectory(parent) && !_api.CanWrite(parent))
            {
                SetStatus("Permission denied - cannot save");
                return;
            }
        }

        var sb = new StringBuilder();
        for (int i = 0; i < _lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(_lines[i]);
        }

        string content = sb.ToString();
        _api.WriteText(_filePath, content);
        _api.Save();

        _modified = false;
        int bytes = Encoding.UTF8.GetByteCount(content);
        SetStatus("Saved " + _filePath + " (" + _lines.Count + " lines, " + bytes + " bytes)");
    }

    // ?? Rendering ?????????????????????????????????????????????????

    private static void Render()
    {
        _editorRows = NetNIX.Shell.SessionIO.WindowHeight - 3; // 1 title + 1 status + 1 shortcut bar
        _editorCols = NetNIX.Shell.SessionIO.WindowWidth;
        if (_editorRows < 1) _editorRows = 1;

        // Ensure cursor is visible (vertical)
        if (_cursorRow < _scrollOffset)
            _scrollOffset = _cursorRow;
        if (_cursorRow >= _scrollOffset + _editorRows)
            _scrollOffset = _cursorRow - _editorRows + 1;

        // Ensure cursor is visible (horizontal)
        int textArea = _editorCols - GutterWidth;
        if (textArea < 1) textArea = 1;
        if (_cursorCol < _scrollX)
            _scrollX = _cursorCol;
        if (_cursorCol >= _scrollX + textArea)
            _scrollX = _cursorCol - textArea + 1;

        var buf = new StringBuilder();

        // Disable cursor flicker
        buf.Append("\x1b[?25l");
        buf.Append("\x1b[H"); // move to top-left

        // ?? Title bar ?????????????????????????????????????????????
        string name = _api.GetName(_filePath);
        string title = " " + EditorName + " - " + name + (_modified ? " [modified]" : "");
        string lineInfo = "Ln " + (_cursorRow + 1) + ", Col " + (_cursorCol + 1) + " ";
        int pad = _editorCols - title.Length - lineInfo.Length;
        if (pad < 0) pad = 0;
        buf.Append("\x1b[7m"); // inverse video
        buf.Append(title);
        buf.Append(new string(' ', pad));
        buf.Append(lineInfo);
        buf.Append("\x1b[0m");
        buf.AppendLine();

        // ?? Editor lines ??????????????????????????????????????????
        for (int i = 0; i < _editorRows; i++)
        {
            int lineIdx = _scrollOffset + i;
            buf.Append("\x1b[K"); // clear line

            if (lineIdx < _lines.Count)
            {
                string lineText = _lines[lineIdx].ToString();
                // Line number gutter
                string num = (lineIdx + 1).ToString();
                while (num.Length < 4) num = " " + num;
                buf.Append("\x1b[90m"); // dim
                buf.Append(num);
                buf.Append(_scrollX > 0 ? "<| " : " | ");
                buf.Append("\x1b[0m");

                if (textArea > 0 && lineText.Length > _scrollX)
                {
                    string visible = lineText.Substring(_scrollX);
                    if (visible.Length > textArea)
                        visible = visible.Substring(0, textArea);
                    buf.Append(visible);
                }
            }
            else
            {
                buf.Append("\x1b[90m   ~ \x1b[0m");
            }

            buf.AppendLine();
        }

        // ?? Status message ????????????????????????????????????????
        buf.Append("\x1b[K");
        if ((DateTime.Now - _statusTime).TotalSeconds < StatusTimeout && _statusMessage.Length > 0)
        {
            buf.Append("\x1b[33m"); // yellow
            string msg = _statusMessage.Length > _editorCols
                ? _statusMessage.Substring(0, _editorCols) : _statusMessage;
            buf.Append(msg);
            buf.Append("\x1b[0m");
        }
        buf.AppendLine();

        // ?? Shortcut bar ??????????????????????????????????????????
        buf.Append("\x1b[K");
        buf.Append("\x1b[7m"); // inverse
        string bar = " F2/^W Save  |  ^Q Quit  |  ^G Go to line  |  ^K Cut line  |  ^U Paste line  |  ^T Template ";
        if (bar.Length > _editorCols)
            bar = bar.Substring(0, _editorCols);
        else
            bar = bar + new string(' ', _editorCols - bar.Length);
        buf.Append(bar);
        buf.Append("\x1b[0m");

        // ?? Position cursor ???????????????????????????????????????
        int screenRow = _cursorRow - _scrollOffset + 2; // +1 title, +1 one-based
        int screenCol = _cursorCol - _scrollX + GutterWidth + 1;
        buf.Append("\x1b[" + screenRow + ";" + screenCol + "H");
        buf.Append("\x1b[?25h"); // show cursor

        Console.Write(buf.ToString());
    }

    // ?? Input handling ????????????????????????????????????????????

    private static void ProcessKey()
    {
        var key = NetNIX.Shell.SessionIO.ReadKey(true);
        if (key.Key == ConsoleKey.F2) { SaveFile(); return; }
        if (key.Key == ConsoleKey.F10) { HandleQuit(); return; }

        // Ctrl combinations
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            if (key.Key == ConsoleKey.S) { SaveFile(); return; }
            if (key.Key == ConsoleKey.W) { SaveFile(); return; }
            if (key.Key == ConsoleKey.Q) { HandleQuit(); return; }
            if (key.Key == ConsoleKey.G) { HandleGoToLine(); return; }
            if (key.Key == ConsoleKey.K) { HandleCutLine(); return; }
            if (key.Key == ConsoleKey.U) { HandlePasteLine(); return; }
            if (key.Key == ConsoleKey.T) { HandleTemplate(); return; }
        }

        // Fallback: match by KeyChar for terminals that don't set Modifiers
        if (key.KeyChar < 32 && key.KeyChar > 0)
        {
            if (key.KeyChar == '\x13') { SaveFile(); return; }       // Ctrl+S
            if (key.KeyChar == '\x17') { SaveFile(); return; }       // Ctrl+W
            if (key.KeyChar == '\x11') { HandleQuit(); return; }     // Ctrl+Q
            if (key.KeyChar == '\x07') { HandleGoToLine(); return; } // Ctrl+G
            if (key.KeyChar == '\x0B') { HandleCutLine(); return; }  // Ctrl+K
            if (key.KeyChar == '\x15') { HandlePasteLine(); return; }// Ctrl+U
            if (key.KeyChar == '\x14') { HandleTemplate(); return; } // Ctrl+T
        }

        if (key.Key == ConsoleKey.UpArrow)
        {
            if (_cursorRow > 0) { _cursorRow--; ClampCol(); }
            return;
        }
        if (key.Key == ConsoleKey.DownArrow)
        {
            if (_cursorRow < _lines.Count - 1) { _cursorRow++; ClampCol(); }
            return;
        }
        if (key.Key == ConsoleKey.LeftArrow)
        {
            if (_cursorCol > 0)
                _cursorCol--;
            else if (_cursorRow > 0)
            {
                _cursorRow--;
                _cursorCol = _lines[_cursorRow].Length;
            }
            return;
        }
        if (key.Key == ConsoleKey.RightArrow)
        {
            if (_cursorCol < _lines[_cursorRow].Length)
                _cursorCol++;
            else if (_cursorRow < _lines.Count - 1)
            {
                _cursorRow++;
                _cursorCol = 0;
            }
            return;
        }
        if (key.Key == ConsoleKey.Home) { _cursorCol = 0; return; }
        if (key.Key == ConsoleKey.End) { _cursorCol = _lines[_cursorRow].Length; return; }
        if (key.Key == ConsoleKey.PageUp)
        {
            _cursorRow = Math.Max(0, _cursorRow - _editorRows);
            ClampCol();
            return;
        }
        if (key.Key == ConsoleKey.PageDown)
        {
            _cursorRow = Math.Min(_lines.Count - 1, _cursorRow + _editorRows);
            ClampCol();
            return;
        }
        if (key.Key == ConsoleKey.Enter) { InsertNewline(); return; }
        if (key.Key == ConsoleKey.Backspace) { HandleBackspace(); return; }
        if (key.Key == ConsoleKey.Delete) { HandleDelete(); return; }
        if (key.Key == ConsoleKey.Tab) { InsertText("    "); return; }
        if (key.Key == ConsoleKey.Escape) { return; } // ignore

        // Printable character
        if (key.KeyChar >= 32 && key.KeyChar < 127)
        {
            InsertChar(key.KeyChar);
        }
    }

    // ?? Editing operations ????????????????????????????????????????

    private static void InsertChar(char c)
    {
        _lines[_cursorRow].Insert(_cursorCol, c);
        _cursorCol++;
        _modified = true;
    }

    private static void InsertText(string text)
    {
        _suppressAutoIndent = true;
        try
        {
            foreach (char c in text)
            {
                if (c == '\n')
                    InsertNewline();
                else
                    InsertChar(c);
            }
        }
        finally
        {
            _suppressAutoIndent = false;
        }
    }

    private static void InsertNewline()
    {
        string currentLine = _lines[_cursorRow].ToString();
        string before = currentLine.Substring(0, _cursorCol);
        string after = currentLine.Substring(_cursorCol);

        // Auto-indent: copy leading whitespace (unless suppressed)
        string indent = "";
        if (!_suppressAutoIndent)
        {
            foreach (char ch in before)
            {
                if (ch == ' ') indent += " ";
                else break;
            }
        }

        _lines[_cursorRow] = new StringBuilder(before);
        _lines.Insert(_cursorRow + 1, new StringBuilder(indent + after));
        _cursorRow++;
        _cursorCol = indent.Length;
        _modified = true;
    }

    private static void HandleBackspace()
    {
        if (_cursorCol > 0)
        {
            _lines[_cursorRow].Remove(_cursorCol - 1, 1);
            _cursorCol--;
            _modified = true;
        }
        else if (_cursorRow > 0)
        {
            // Merge with previous line
            int prevLen = _lines[_cursorRow - 1].Length;
            _lines[_cursorRow - 1].Append(_lines[_cursorRow]);
            _lines.RemoveAt(_cursorRow);
            _cursorRow--;
            _cursorCol = prevLen;
            _modified = true;
        }
    }

    private static void HandleDelete()
    {
        if (_cursorCol < _lines[_cursorRow].Length)
        {
            _lines[_cursorRow].Remove(_cursorCol, 1);
            _modified = true;
        }
        else if (_cursorRow < _lines.Count - 1)
        {
            // Merge next line into current
            _lines[_cursorRow].Append(_lines[_cursorRow + 1]);
            _lines.RemoveAt(_cursorRow + 1);
            _modified = true;
        }
    }

    // ?? Cut / Paste ???????????????????????????????????????????????

    private static void HandleCutLine()
    {
        _clipboard.Add(_lines[_cursorRow].ToString());
        if (_lines.Count > 1)
        {
            _lines.RemoveAt(_cursorRow);
            if (_cursorRow >= _lines.Count)
                _cursorRow = _lines.Count - 1;
        }
        else
        {
            _lines[0].Clear();
        }
        ClampCol();
        _modified = true;
        SetStatus("Line cut to clipboard");
    }

    private static void HandlePasteLine()
    {
        if (_clipboard.Count == 0)
        {
            SetStatus("Clipboard is empty");
            return;
        }

        string last = _clipboard[_clipboard.Count - 1];
        _lines.Insert(_cursorRow + 1, new StringBuilder(last));
        _cursorRow++;
        _cursorCol = 0;
        _modified = true;
        SetStatus("Line pasted");
    }

    // ?? Go to line ????????????????????????????????????????????????

    private static void HandleGoToLine()
    {
        string input = Prompt("Go to line: ");
        if (input != null)
        {
            int lineNum;
            if (int.TryParse(input, out lineNum))
            {
                if (lineNum < 1) lineNum = 1;
                if (lineNum > _lines.Count) lineNum = _lines.Count;
                _cursorRow = lineNum - 1;
                _cursorCol = 0;
            }
        }
    }

    // ?? Quit ??????????????????????????????????????????????????????

    private static void HandleQuit()
    {
        if (_modified)
        {
            string response = Prompt("Unsaved changes! Save before exit? (y/n/cancel): ");
            if (response == null || response.StartsWith("c", StringComparison.OrdinalIgnoreCase))
                return;
            if (response.StartsWith("y", StringComparison.OrdinalIgnoreCase))
                SaveFile();
        }
        _quit = true;
    }

    // ?? C# Templates ??????????????????????????????????????????????

    private static void HandleTemplate()
    {
        string choice = Prompt("Template: (1) Script  (2) Class  (3) Main  (4) Snippet: ");
        if (choice == null) return;

        string template = "";
        if (choice == "1") template = ScriptTemplate();
        else if (choice == "2") template = ClassTemplate();
        else if (choice == "3") template = MainTemplate();
        else if (choice == "4") template = SnippetTemplate();

        if (template.Length == 0)
        {
            SetStatus("Unknown template");
            return;
        }

        InsertText(template);
        SetStatus("Template inserted");
    }

    private static string ScriptTemplate()
    {
        return "using System;\nusing System.Linq;\nusing NetNIX.Scripting;\n\npublic static class MyCommand\n{\n    public static int Run(NixApi api, string[] args)\n    {\n        // Your code here\n        Console.WriteLine(\"Hello from script!\");\n        return 0;\n    }\n}\n";
    }

    private static string ClassTemplate()
    {
        return "using System;\n\npublic class MyClass\n{\n    public string Name { get; set; }\n\n    public MyClass(string name)\n    {\n        Name = name;\n    }\n\n    public override string ToString() => Name;\n}\n";
    }

    private static string MainTemplate()
    {
        return "using System;\nusing System.Linq;\nusing NetNIX.Scripting;\n\npublic static class Program\n{\n    public static int Run(NixApi api, string[] args)\n    {\n        if (args.Length == 0)\n        {\n            Console.WriteLine(\"Usage: <command> [args...]\");\n            return 1;\n        }\n\n        foreach (var arg in args)\n            Console.WriteLine(arg);\n\n        return 0;\n    }\n}\n";
    }

    private static string SnippetTemplate()
    {
        return "        // Read a file\n        string content = api.ReadText(\"myfile.txt\");\n\n        // Write a file\n        api.WriteText(\"output.txt\", \"Hello World\\n\");\n\n        // List directory\n        foreach (var path in api.ListDirectory(\".\"))\n            Console.WriteLine(api.NodeName(path));\n\n        // Check permissions\n        if (api.CanRead(\"somefile\"))\n            Console.WriteLine(api.ReadText(\"somefile\"));\n\n        // Save filesystem\n        api.Save();\n";
    }

    // ?? Helpers ????????????????????????????????????????????????????

    private static void ClampCol()
    {
        if (_cursorCol > _lines[_cursorRow].Length)
            _cursorCol = _lines[_cursorRow].Length;
    }

    private static void SetStatus(string msg)
    {
        _statusMessage = msg;
        _statusTime = DateTime.Now;
    }

    private static string Prompt(string message)
    {
        // Draw prompt on the status line
        int promptRow = NetNIX.Shell.SessionIO.WindowHeight - 2;
        Console.Write("\x1b[" + (promptRow + 1) + ";1H"); // move to prompt row
        Console.Write("\x1b[K"); // clear line
        Console.Write("\x1b[33m" + message + "\x1b[0m");
        try { Console.CursorVisible = true; } catch { }

        var sb = new StringBuilder();
        while (true)
        {
            var key = NetNIX.Shell.SessionIO.ReadKey(true);

            if (key.Key == ConsoleKey.Escape)
            {
                try { Console.CursorVisible = false; } catch { }
                return null;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                try { Console.CursorVisible = false; } catch { }
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Remove(sb.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
            else if (key.KeyChar >= 32)
            {
                sb.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("nedit - NetNIX text editor");
        Console.WriteLine();
        Console.WriteLine("Usage: edit <file>");
        Console.WriteLine();
        Console.WriteLine("Keyboard shortcuts:");
        Console.WriteLine("  F2 / Ctrl+W     Save file");
        Console.WriteLine("  Ctrl+Q          Quit (prompts if unsaved changes)");
        Console.WriteLine("  Ctrl+G          Go to line number");
        Console.WriteLine("  Ctrl+K          Cut current line to clipboard");
        Console.WriteLine("  Ctrl+U          Paste last cut line below cursor");
        Console.WriteLine("  Ctrl+T          Insert a C# code template");
        Console.WriteLine("  Arrow keys      Move cursor");
        Console.WriteLine("  Home / End      Start / end of current line");
        Console.WriteLine("  Page Up/Down    Scroll by one page");
        Console.WriteLine("  Tab             Insert 4 spaces");
        Console.WriteLine("  Enter           New line with auto-indent");
        Console.WriteLine();
        Console.WriteLine("Templates (Ctrl+T):");
        Console.WriteLine("  1) Script    NetNIX command skeleton");
        Console.WriteLine("  2) Class     Basic C# class");
        Console.WriteLine("  3) Main      Full command with arg handling");
        Console.WriteLine("  4) Snippet   Common NixApi patterns");
        Console.WriteLine();
        Console.WriteLine("See also: man editor");
    }
}
