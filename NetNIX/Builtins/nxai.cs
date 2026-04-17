using System;
using System.Collections.Generic;
using System.Globalization;
using NetNIX.Scripting;

#include <settingslib>
#include <koboldlib>
#include <aihorde>
#include <nxailib>

/// <summary>
/// nxai — Unified AI chat client for NetNIX.
///
/// Supports both KoboldCpp and AI Horde backends with seamless switching.
///
/// Usage:
///     nxai                        Start interactive chat
///     nxai -m "message"           Send a single message
///     nxai --status               Check API connection
///     nxai --config               Show current settings
///     nxai --set key value        Change an nxai setting (per-user)
///     nxai --backend kobold       Switch backend to KoboldCpp
///     nxai --backend horde        Switch backend to AI Horde
///     nxai --reset                Reset nxai settings to defaults
///     nxai --help                 Show help
/// </summary>
public static class NxAiCommand
{
    public static int Run(NixApi api, string[] args)
    {
        var argList = new List<string>(args);

        if (argList.Count == 0 && !Console.IsInputRedirected)
        {
            // No args — launch interactive mode
        }
        else if (argList.Contains("-h") || argList.Contains("--help"))
        {
            PrintUsage();
            return 0;
        }

        // --status
        if (argList.Contains("--status"))
            return CmdStatus(api);

        // --config
        if (argList.Contains("--config"))
            return CmdConfig(api);

        // --backend <name>
        int backIdx = argList.IndexOf("--backend");
        if (backIdx >= 0)
        {
            if (backIdx + 1 >= argList.Count)
            {
                Console.WriteLine("nxai: --backend requires 'kobold' or 'horde'");
                return 1;
            }
            return CmdSetBackend(api, argList[backIdx + 1]);
        }

        // --set key value
        int setIdx = argList.IndexOf("--set");
        if (setIdx >= 0)
        {
            if (setIdx + 2 >= argList.Count)
            {
                Console.WriteLine("nxai: --set requires a key and value");
                return 1;
            }
            return CmdSet(api, argList[setIdx + 1], argList[setIdx + 2]);
        }

        // --reset
        if (argList.Contains("--reset"))
            return CmdReset(api);

        // -m "message"
        int msgIdx = argList.IndexOf("-m");
        if (msgIdx < 0) msgIdx = argList.IndexOf("--message");
        if (msgIdx >= 0)
        {
            if (msgIdx + 1 >= argList.Count)
            {
                Console.WriteLine("nxai: -m requires a message");
                return 1;
            }
            return CmdSingleMessage(api, argList[msgIdx + 1]);
        }

        // Default: interactive chat
        return CmdChat(api);
    }

    // --- Interactive chat ---

    private static int CmdChat(NixApi api)
    {
        var ai = new NxAi(api);

        Console.Write("Connecting to " + ai.Backend + " backend...");

        if (!ai.IsAvailable())
        {
            Console.WriteLine(" FAILED");
            Console.WriteLine();
            Console.WriteLine("Could not reach the AI backend.");
            Console.WriteLine("  Backend:  " + ai.Backend);
            Console.WriteLine("  Endpoint: " + ai.Endpoint);
            Console.WriteLine();
            Console.WriteLine("  Switch backend:  nxai --backend <kobold|horde>");
            Console.WriteLine("  Check settings:  nxai --config");
            return 1;
        }

        Console.WriteLine(" OK");
        Console.WriteLine(ai.GetBackendInfo());

        if (ai.Backend == "horde")
        {
            var horde = ai.GetHordeApi();
            if (horde != null && horde.ApiKey == "0000000000")
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Warning: Using anonymous AI Horde key. Requests may be slow.");
                Console.WriteLine("  Set key: aihorde --set api_key <your-key>");
                Console.ResetColor();
            }
        }

        Console.WriteLine();
        Console.WriteLine("Type your messages below. Commands:");
        Console.WriteLine("  /quit, /exit         End the chat");
        Console.WriteLine("  /clear               Clear conversation history");
        Console.WriteLine("  /config              Show settings");
        Console.WriteLine("  /backend <name>      Switch backend (kobold/horde)");
        Console.WriteLine("  /set key value       Change a setting for this session");
        Console.WriteLine("  /system <prompt>     Change system prompt for this session");
        Console.WriteLine("  /save [path]         Save conversation to a file");
        Console.WriteLine("  /help                Show these commands");
        Console.WriteLine();

        var chat = new NxAiChat(ai);

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("You> ");
            Console.ResetColor();

            string input = Console.ReadLine();
            if (input == null) break;

            input = input.Trim();
            if (input.Length == 0) continue;

            if (input.StartsWith("/"))
            {
                string cmd = input.ToLowerInvariant();
                if (cmd == "/quit" || cmd == "/exit" || cmd == "/q")
                    break;

                if (cmd == "/clear")
                {
                    chat.Clear();
                    Console.WriteLine("Conversation cleared.");
                    Console.WriteLine();
                    continue;
                }

                if (cmd == "/config")
                {
                    PrintCurrentSettings(ai);
                    Console.WriteLine();
                    continue;
                }

                if (cmd == "/help")
                {
                    Console.WriteLine("  /quit, /exit         End the chat");
                    Console.WriteLine("  /clear               Clear conversation history");
                    Console.WriteLine("  /config              Show settings");
                    Console.WriteLine("  /backend <name>      Switch backend (kobold/horde)");
                    Console.WriteLine("  /set key value       Change a setting for this session");
                    Console.WriteLine("  /system              Show current system prompt");
                    Console.WriteLine("  /system <prompt>     Change system prompt for this session");
                    Console.WriteLine("  /save [path]         Save conversation to a file");
                    Console.WriteLine("  /help                Show these commands");
                    Console.WriteLine();
                    continue;
                }

                if (cmd.StartsWith("/backend "))
                {
                    string newBackend = input.Substring(9).Trim().ToLowerInvariant();
                    if (newBackend != "kobold" && newBackend != "horde")
                    {
                        Console.WriteLine("Unknown backend. Use 'kobold' or 'horde'.");
                    }
                    else
                    {
                        ai.SwitchBackend(newBackend);
                        Console.Write("Switched to " + newBackend + "...");
                        if (ai.IsAvailable())
                        {
                            Console.WriteLine(" OK");
                            Console.WriteLine(ai.GetBackendInfo());
                        }
                        else
                        {
                            Console.WriteLine(" (not reachable — will retry on next message)");
                        }
                    }
                    Console.WriteLine();
                    continue;
                }

                if (cmd == "/system")
                {
                    Console.WriteLine("System prompt:");
                    Console.WriteLine(ai.SystemPrompt);
                    Console.WriteLine();
                    continue;
                }

                if (cmd.StartsWith("/system "))
                {
                    string newPrompt = input.Substring(8).Trim();
                    ai.SystemPrompt = newPrompt;
                    ai.SyncSystemPrompt();
                    Console.WriteLine("System prompt updated (session only).");
                    Console.WriteLine();
                    continue;
                }

                if (cmd.StartsWith("/set "))
                {
                    HandleInlineSet(ai, input.Substring(5).Trim());
                    continue;
                }

                if (cmd.StartsWith("/save"))
                {
                    HandleSave(api, chat, input);
                    continue;
                }

                Console.WriteLine("Unknown command: " + input);
                Console.WriteLine("Type /help for available commands.");
                Console.WriteLine();
                continue;
            }

            // Send message
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("AI> ");
            Console.ResetColor();

            string reply = chat.Say(input);

            if (reply == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Error: no response from " + ai.Backend + " backend]");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine(reply);
            }
            Console.WriteLine();
        }

        Console.WriteLine("Goodbye.");
        return 0;
    }

    // --- Single message ---

    private static int CmdSingleMessage(NixApi api, string message)
    {
        var ai = new NxAi(api);

        if (!ai.IsAvailable())
        {
            Console.Error.WriteLine("nxai: cannot reach " + ai.Backend + " backend at " + ai.Endpoint);
            return 1;
        }

        var chat = new NxAiChat(ai);
        string reply = chat.Say(message);

        if (reply == null)
        {
            Console.Error.WriteLine("nxai: no response from " + ai.Backend + " backend");
            return 1;
        }

        Console.WriteLine(reply);
        return 0;
    }

    // --- Status ---

    private static int CmdStatus(NixApi api)
    {
        var ai = new NxAi(api);

        Console.WriteLine("NxAI Status");
        Console.WriteLine("  Backend:   " + ai.Backend);
        Console.WriteLine("  Endpoint:  " + ai.Endpoint);
        Console.Write("  Reachable: ");

        if (!ai.IsAvailable())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("NO");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("YES");
        Console.ResetColor();

        Console.WriteLine("  " + ai.GetBackendInfo());
        return 0;
    }

    // --- Config ---

    private static int CmdConfig(NixApi api)
    {
        var ai = new NxAi(api);

        string home = api.Uid == 0 ? "/root" : "/home/" + api.Username;
        string nxaiPath = home + "/.config/nxai.conf";

        Console.WriteLine("NxAI Settings");
        Console.WriteLine();
        Console.WriteLine("  NxAI config:   " + nxaiPath + (api.IsFile(nxaiPath) ? "" : " (not created)"));
        Console.WriteLine();

        Console.WriteLine("  [nxai]");
        Console.WriteLine("    backend        = " + ai.Backend);
        Console.WriteLine("    system_prompt  = " + Truncate(ai.SystemPrompt, 50));
        Console.WriteLine();

        Console.WriteLine("  [" + ai.Backend + " backend settings]");
        if (ai.Backend == "horde")
        {
            var h = ai.GetHordeApi();
            Console.WriteLine("    endpoint       = " + h.Endpoint);
            Console.WriteLine("    api_key        = " + (h.ApiKey == "0000000000" ? "(anonymous)" : "****" + h.ApiKey.Substring(Math.Max(0, h.ApiKey.Length - 4))));
            Console.WriteLine("    max_length     = " + h.MaxLength);
            Console.WriteLine("    max_context    = " + h.MaxContext);
            Console.WriteLine("    temperature    = " + h.Temperature.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("    top_p          = " + h.TopP.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("    top_k          = " + h.TopK);
            Console.WriteLine("    rep_penalty    = " + h.RepPenalty.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("    model          = " + (string.IsNullOrEmpty(h.Model) ? "(any)" : h.Model));
            Console.WriteLine("    poll_interval  = " + h.PollInterval);
            Console.WriteLine("    max_wait       = " + h.MaxWait);
        }
        else
        {
            var k = ai.GetKoboldApi();
            Console.WriteLine("    endpoint       = " + k.Endpoint);
            Console.WriteLine("    max_context    = " + k.MaxContext);
            Console.WriteLine("    max_length     = " + k.MaxLength);
            Console.WriteLine("    temperature    = " + k.Temperature.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("    top_p          = " + k.TopP.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("    top_k          = " + k.TopK);
            Console.WriteLine("    rep_penalty    = " + k.RepPenalty.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("    timeout        = " + k.Timeout);
            Console.WriteLine("    use_api_key    = " + k.UseApiKey);
            Console.WriteLine("    api_key        = " + (k.UseApiKey && !string.IsNullOrEmpty(k.ApiKey) ? "****" + k.ApiKey.Substring(Math.Max(0, k.ApiKey.Length - 4)) : "(not set)"));
        }

        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  nxai --set <key> <value>    Change an nxai or backend setting");
        Console.WriteLine("  nxai --backend <name>       Switch backend (kobold/horde)");
        Console.WriteLine("  nxai --reset                Reset nxai settings to defaults");
        Console.WriteLine();
        Console.WriteLine("Backend settings are managed by their own commands:");
        Console.WriteLine("  kobold --set <key> <value>  Change KoboldCpp settings");
        Console.WriteLine("  aihorde --set <key> <value> Change AI Horde settings");

        return 0;
    }

    // --- Set ---

    private static int CmdSet(NixApi api, string key, string value)
    {
        // nxai's own keys
        var nxaiKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "backend", "system_prompt"
        };

        if (nxaiKeys.Contains(key))
        {
            if (key.Equals("backend", StringComparison.OrdinalIgnoreCase))
            {
                string v = value.ToLowerInvariant();
                if (v != "kobold" && v != "horde")
                {
                    Console.WriteLine("nxai: backend must be 'kobold' or 'horde'");
                    return 1;
                }
                value = v;
            }

            Settings.Set(api, NxAi.AppName, key, value);
            Console.WriteLine("nxai: " + key + " = " + value);
            return 0;
        }

        // Backend-specific keys — route to the active backend's settings
        var ai = new NxAi(api);

        var koboldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "endpoint", "max_context", "max_length", "temperature",
            "top_p", "top_k", "rep_penalty", "timeout",
            "use_api_key", "api_key"
        };

        var hordeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "endpoint", "api_key", "max_length", "max_context", "temperature",
            "top_p", "top_k", "rep_penalty", "model", "poll_interval", "max_wait"
        };

        bool isKobold = ai.Backend == "kobold";
        var activeKeys = isKobold ? koboldKeys : hordeKeys;
        string activeApp = isKobold ? KoboldApi.AppName : HordeApi.AppName;

        if (activeKeys.Contains(key))
        {
            Settings.Set(api, activeApp, key, value);
            Console.WriteLine("nxai: [" + ai.Backend + "] " + key + " = " + value);
            return 0;
        }

        Console.WriteLine("nxai: unknown setting '" + key + "'");
        Console.WriteLine("nxai keys: backend, system_prompt");
        Console.WriteLine(ai.Backend + " keys: " + string.Join(", ", activeKeys));
        return 1;
    }

    // --- Set backend shortcut ---

    private static int CmdSetBackend(NixApi api, string backend)
    {
        return CmdSet(api, "backend", backend);
    }

    // --- Reset ---

    private static int CmdReset(NixApi api)
    {
        foreach (var key in NxAi.Defaults.Keys)
            Settings.Remove(api, NxAi.AppName, key);

        var ai = new NxAi(api);
        Console.WriteLine("nxai: settings reset to defaults");
        Console.WriteLine("  backend       = " + ai.Backend);
        Console.WriteLine("  system_prompt = " + Truncate(ai.SystemPrompt, 60));
        return 0;
    }

    // --- Helpers ---

    private static void PrintCurrentSettings(NxAi ai)
    {
        Console.WriteLine("  backend        = " + ai.Backend);
        Console.WriteLine("  system_prompt  = " + Truncate(ai.SystemPrompt, 50));
        Console.WriteLine("  endpoint       = " + ai.Endpoint);
        Console.WriteLine("  max_length     = " + ai.MaxLength);
        Console.WriteLine("  max_context    = " + ai.MaxContext);
        Console.WriteLine("  temperature    = " + ai.Temperature.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("  top_p          = " + ai.TopP.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("  top_k          = " + ai.TopK);
        Console.WriteLine("  rep_penalty    = " + ai.RepPenalty.ToString(CultureInfo.InvariantCulture));
    }

    private static void HandleInlineSet(NxAi ai, string args)
    {
        int space = args.IndexOf(' ');
        if (space <= 0)
        {
            Console.WriteLine("Usage: /set <key> <value>");
            return;
        }

        string key = args.Substring(0, space).Trim().ToLowerInvariant();
        string value = args.Substring(space + 1).Trim();

        switch (key)
        {
            case "system_prompt":
                ai.SystemPrompt = value;
                ai.SyncSystemPrompt();
                break;
            case "backend":
                if (value != "kobold" && value != "horde")
                {
                    Console.WriteLine("Backend must be 'kobold' or 'horde'.");
                    return;
                }
                ai.SwitchBackend(value);
                break;
            default:
                Console.WriteLine("Unknown nxai setting: " + key);
                Console.WriteLine("Session keys: system_prompt, backend");
                return;
        }

        Console.WriteLine("  " + key + " = " + value + " (session only — use 'nxai --set' to persist)");
    }

    private static void HandleSave(NixApi api, NxAiChat chat, string input)
    {
        string path = null;
        if (input.Length > 5)
            path = input.Substring(5).Trim();

        if (string.IsNullOrEmpty(path))
            path = "~/nxai-chat.txt";

        if (path.StartsWith("~/"))
            path = (api.Uid == 0 ? "/root" : "/home/" + api.Username) + path.Substring(1);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# NxAI Chat Log — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("# Backend: " + chat.Ai.Backend);
        sb.AppendLine();

        foreach (var (role, text) in chat.History)
        {
            sb.AppendLine(role == "user" ? "You> " + text : "AI> " + text);
            sb.AppendLine();
        }

        try
        {
            api.WriteText(path, sb.ToString());
            Console.WriteLine("Conversation saved to " + path);
        }
        catch (Exception ex)
        {
            Console.WriteLine("nxai: failed to save: " + ex.Message);
        }
        Console.WriteLine();
    }

    private static string Truncate(string s, int maxLen)
    {
        if (s == null) return "";
        if (s.Length <= maxLen) return s;
        return s.Substring(0, maxLen) + "...";
    }

    private static void PrintUsage()
    {
        Console.WriteLine("nxai — Unified AI chat client for NetNIX");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  nxai                        Start interactive chat");
        Console.WriteLine("  nxai -m \"message\"           Send a single message");
        Console.WriteLine("  nxai --status               Check API connection");
        Console.WriteLine("  nxai --config               Show all settings");
        Console.WriteLine("  nxai --set key value        Change an nxai setting");
        Console.WriteLine("  nxai --backend <name>       Switch backend (kobold/horde)");
        Console.WriteLine("  nxai --reset                Reset nxai settings to defaults");
        Console.WriteLine("  nxai --help                 Show this help");
        Console.WriteLine();
        Console.WriteLine("Backends:");
        Console.WriteLine("  kobold   KoboldCpp (local)    Settings: kobold --config");
        Console.WriteLine("  horde    AI Horde (remote)    Settings: aihorde --config");
        Console.WriteLine();
        Console.WriteLine("NxAI settings (app name 'nxai'):");
        Console.WriteLine("  backend        Active backend        (default: kobold)");
        Console.WriteLine("  system_prompt  Default system prompt (default: built-in)");
        Console.WriteLine();
        Console.WriteLine("All other generation settings (endpoint, temperature, etc) are");
        Console.WriteLine("read from the active backend's own settings. Configure them with:");
        Console.WriteLine("  kobold --set <key> <value>");
        Console.WriteLine("  aihorde --set <key> <value>");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  nxai --backend horde");
        Console.WriteLine("  nxai --set system_prompt \"You are a pirate.\"");
        Console.WriteLine("  nxai -m \"Tell me a joke\"");
        Console.WriteLine("  nxai --config");
        Console.WriteLine("  nxai --status");
        Console.WriteLine("  nxai");
        Console.WriteLine();
        Console.WriteLine("Interactive commands:");
        Console.WriteLine("  /quit, /exit         End the chat");
        Console.WriteLine("  /clear               Clear conversation history");
        Console.WriteLine("  /config              Show settings");
        Console.WriteLine("  /backend <name>      Switch backend mid-session");
        Console.WriteLine("  /system <prompt>     Change system prompt for this session");
        Console.WriteLine("  /set key value       Change a setting for this session");
        Console.WriteLine("  /save [path]         Save conversation to a file");
        Console.WriteLine("  /help                Show commands");
        Console.WriteLine();
        Console.WriteLine("For scripting, use nxailib directly:");
        Console.WriteLine("  #include <nxailib>");
        Console.WriteLine("  var chat = new NxAiChat(api, \"You are a pirate.\");");
        Console.WriteLine("  chat.Say(\"Hello!\");");
        Console.WriteLine("  Console.WriteLine(chat.LastReply);");
        Console.WriteLine();
        Console.WriteLine("On first use, settings are installed to:");
        Console.WriteLine("  ~/.config/nxai.conf    (per-user)");
        Console.WriteLine("  /etc/opt/nxai.conf     (system-wide, if root)");
        Console.WriteLine();
        Console.WriteLine("See also: man nxailib, man kobold, man aihorde, man settingslib");
    }
}
