using System;
using System.Collections.Generic;
using NetNIX.Scripting;
#include <settingslib>

/// <summary>
/// settings-demo — demonstrates the settingslib API for per-user
/// and system-wide application settings.
/// </summary>
public static class SettingsDemoCommand
{
    private const string AppName = "demo-app";

    public static int Run(NixApi api, string[] args)
    {
        if (args.Length > 0 && (args[0] == "-h" || args[0] == "--help"))
        {
            Console.WriteLine("settings-demo — demonstrate the settingslib settings API");
            Console.WriteLine("Usage: settings-demo");
            Console.WriteLine();
            Console.WriteLine("Walks through per-user and system-wide settings.");
            Console.WriteLine("System-wide steps require root/sudo.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("\u001b[1;36m=== Settings Library Demo ===\u001b[0m");
        Console.WriteLine($"  Running as: {api.Username} (uid {api.Uid})");
        Console.WriteLine();

        // ── Per-user settings ──────────────────────────────────────────

        Step("1", "Set per-user settings");
        Settings.Set(api, AppName, "theme", "dark");
        Settings.Set(api, AppName, "language", "en");
        Settings.Set(api, AppName, "font_size", "14");
        Console.WriteLine("  Settings.Set(api, \"demo-app\", \"theme\", \"dark\");");
        Console.WriteLine("  Settings.Set(api, \"demo-app\", \"language\", \"en\");");
        Console.WriteLine("  Settings.Set(api, \"demo-app\", \"font_size\", \"14\");");
        Console.WriteLine("  \u001b[32mDone — saved to ~/.config/demo-app.conf\u001b[0m");
        Console.WriteLine();

        Step("2", "Read per-user settings");
        string theme = Settings.Get(api, AppName, "theme");
        string lang = Settings.Get(api, AppName, "language");
        string fontSize = Settings.Get(api, AppName, "font_size");
        Console.WriteLine($"  theme     = {theme}");
        Console.WriteLine($"  language  = {lang}");
        Console.WriteLine($"  font_size = {fontSize}");
        Console.WriteLine();

        Step("3", "Read with default fallback");
        string missing = Settings.Get(api, AppName, "nonexistent", "fallback_value");
        Console.WriteLine("  Settings.Get(api, \"demo-app\", \"nonexistent\", \"fallback_value\")");
        Console.WriteLine($"  result = {missing}");
        Console.WriteLine();

        Step("4", "List all per-user settings");
        var all = Settings.GetAll(api, AppName);
        Console.WriteLine($"  {all.Count} setting(s) for '{AppName}':");
        foreach (var kv in all)
            Console.WriteLine($"    {kv.Key} = {kv.Value}");
        Console.WriteLine();

        Step("5", "Remove a setting");
        bool removed = Settings.Remove(api, AppName, "font_size");
        Console.WriteLine($"  Settings.Remove(api, \"demo-app\", \"font_size\") -> {removed}");
        var afterRemove = Settings.GetAll(api, AppName);
        Console.WriteLine($"  Remaining: {afterRemove.Count} setting(s)");
        foreach (var kv in afterRemove)
            Console.WriteLine($"    {kv.Key} = {kv.Value}");
        Console.WriteLine();

        Step("6", "Update an existing setting");
        Settings.Set(api, AppName, "theme", "light");
        Console.WriteLine("  Settings.Set(api, \"demo-app\", \"theme\", \"light\");");
        Console.WriteLine($"  theme = {Settings.Get(api, AppName, "theme")}");
        Console.WriteLine();

        // ── System-wide settings ───────────────────────────────────────

        Step("7", "System-wide settings (requires root)");
        if (api.Uid == 0)
        {
            Settings.SetSystem(api, AppName, "max_connections", "50");
            Settings.SetSystem(api, AppName, "log_level", "info");
            Console.WriteLine("  Settings.SetSystem(api, \"demo-app\", \"max_connections\", \"50\");");
            Console.WriteLine("  Settings.SetSystem(api, \"demo-app\", \"log_level\", \"info\");");
            Console.WriteLine("  \u001b[32mDone — saved to /etc/opt/demo-app.conf\u001b[0m");
        }
        else
        {
            Console.WriteLine("  \u001b[33mSkipped — not running as root.\u001b[0m");
            Console.WriteLine("  Run with: sudo settings-demo");
        }
        Console.WriteLine();

        Step("8", "Read system-wide settings");
        string maxConn = Settings.GetSystem(api, AppName, "max_connections", "(not set)");
        string logLevel = Settings.GetSystem(api, AppName, "log_level", "(not set)");
        Console.WriteLine($"  max_connections = {maxConn}");
        Console.WriteLine($"  log_level       = {logLevel}");
        Console.WriteLine();

        Step("9", "GetEffective — per-user overrides system-wide");
        // Set a system default, then override per-user
        if (api.Uid == 0)
            Settings.SetSystem(api, AppName, "theme", "system-default");
        Console.WriteLine("  System 'theme'  = " + Settings.GetSystem(api, AppName, "theme", "(not set)"));
        Console.WriteLine("  User 'theme'    = " + Settings.Get(api, AppName, "theme", "(not set)"));
        string effective = Settings.GetEffective(api, AppName, "theme", "fallback");
        Console.WriteLine($"  Effective theme = {effective}  (per-user wins)");
        Console.WriteLine();

        Step("10", "List apps with settings");
        string[] userApps = Settings.ListApps(api);
        Console.WriteLine($"  Per-user apps: {string.Join(", ", userApps)}");
        string[] systemApps = Settings.ListSystemApps(api);
        Console.WriteLine($"  System apps:   {string.Join(", ", systemApps)}");
        Console.WriteLine();

        //Removed clean up because we want the user to be able to manually review the settings file after the demo
        // ── Cleanup ────────────────────────────────────────────────────
        /*
        Step("11", "Clean up demo settings");
        Settings.Clear(api, AppName);
        Console.WriteLine("  Settings.Clear(api, \"demo-app\")  — per-user cleared");
        if (api.Uid == 0)
        {
            Settings.ClearSystem(api, AppName);
            Console.WriteLine("  Settings.ClearSystem(api, \"demo-app\")  — system cleared");
        }
        api.Save();
        Console.WriteLine();
        */
        Console.WriteLine("\u001b[1;32m=== Demo complete! ===\u001b[0m");
        Console.WriteLine();
        Console.WriteLine("  To use settingslib in your own scripts:");
        Console.WriteLine("    #include <settingslib>");
        Console.WriteLine();
        Console.WriteLine("    Settings.Set(api, \"myapp\", \"key\", \"value\");");
        Console.WriteLine("    string val = Settings.Get(api, \"myapp\", \"key\", \"default\");");
        Console.WriteLine();
        Console.WriteLine("  Per-user:    ~/.config/<app>.conf");
        Console.WriteLine("  System-wide: /etc/opt/<app>.conf  (root only for writes)");
        Console.WriteLine();
        Console.WriteLine("  See 'man settingslib' for full documentation.");
        Console.WriteLine();

        return 0;
    }

    private static void Step(string num, string title)
    {
        Console.WriteLine($"\u001b[1;33mStep {num}:\u001b[0m {title}");
    }
}
