using System;
using System.Collections.Generic;
using System.Globalization;
using NetNIX.Scripting;

#include <settingslib>
#include <aihorde>

/// <summary>
/// aihorde — Interactive AI chat client for the AI Horde.
///
/// Usage:
///     aihorde                    Start interactive chat
///     aihorde --status           Check API connection and kudos
///     aihorde --config           Show current settings with sources
///     aihorde --set key value    Change a setting (per-user)
///     aihorde --reset            Reset all settings to defaults
///     aihorde --models           List available text models
///     aihorde -m "message"       Send a single message
///     aihorde --help             Show help
/// </summary>
public static class AiHordeCommand
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

        // --models
        if (argList.Contains("--models"))
            return CmdModels(api);

        // --set key value
        int setIdx = argList.IndexOf("--set");
        if (setIdx >= 0)
        {
            if (setIdx + 2 >= argList.Count)
            {
                Console.WriteLine("aihorde: --set requires a key and value");
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
                Console.WriteLine("aihorde: -m requires a message");
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
        var horde = new HordeApi(api);

        Console.Write("Connecting to AI Horde...");

        if (!horde.IsAvailable())
        {
            Console.WriteLine(" FAILED");
            Console.WriteLine();
            Console.WriteLine("Could not reach the AI Horde API.");
            Console.WriteLine("  Current endpoint: " + horde.Endpoint);
            Console.WriteLine("  Change with:      aihorde --set endpoint <url>");
            return 1;
        }

        Console.WriteLine(" OK");

        if (horde.ApiKey == "0000000000")
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Warning: Using anonymous API key. Requests may be slow.");
            Console.WriteLine("  Set your key:  aihorde --set api_key <your-key>");
            Console.WriteLine("  Get a key at:  https://aihorde.net/register");
            Console.ResetColor();
        }

        if (!string.IsNullOrEmpty(horde.Model))
            Console.WriteLine("Model: " + horde.Model);
        else
            Console.WriteLine("Model: (any available)");

        Console.WriteLine();
        Console.WriteLine("Type your messages below. Commands:");
        Console.WriteLine("  /quit, /exit     End the chat");
        Console.WriteLine("  /clear           Clear conversation history");
        Console.WriteLine("  /models          List available models");
        Console.WriteLine("  /config          Show settings");
        Console.WriteLine("  /set key value   Change a setting for this session");
        Console.WriteLine("  /save [path]     Save conversation to a file");
        Console.WriteLine("  /help            Show these commands");
        Console.WriteLine();

        var chat = new HordeChat(horde);

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

                if (cmd == "/models")
                {
                    ShowModels(horde);
                    Console.WriteLine();
                    continue;
                }

                if (cmd == "/config")
                {
                    PrintSettings(horde);
                    Console.WriteLine();
                    continue;
                }

                if (cmd == "/help")
                {
                    Console.WriteLine("  /quit, /exit     End the chat");
                    Console.WriteLine("  /clear           Clear conversation history");
                    Console.WriteLine("  /models          List available models");
                    Console.WriteLine("  /config          Show settings");
                    Console.WriteLine("  /set key value   Change a setting for this session");
                    Console.WriteLine("  /save [path]     Save conversation to a file");
                    Console.WriteLine("  /help            Show these commands");
                    Console.WriteLine();
                    continue;
                }

                if (cmd.StartsWith("/set "))
                {
                    HandleInlineSet(horde, input.Substring(5).Trim());
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
                Console.WriteLine("[Error: no response from AI Horde]");
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
        var horde = new HordeApi(api);

        if (!horde.IsAvailable())
        {
            Console.Error.WriteLine("aihorde: cannot reach AI Horde at " + horde.Endpoint);
            return 1;
        }

        var chat = new HordeChat(horde);
        string reply = chat.Say(message);

        if (reply == null)
        {
            Console.Error.WriteLine("aihorde: no response from AI Horde");
            return 1;
        }

        Console.WriteLine(reply);
        return 0;
    }

    // --- Reset ---

    private static int CmdReset(NixApi api)
    {
        foreach (var key in HordeApi.Defaults.Keys)
            Settings.Remove(api, HordeApi.AppName, key);

        var horde = new HordeApi(api);
        Console.WriteLine("aihorde: settings reset to defaults");
        Console.WriteLine();
        PrintSettingsDetailed(api, horde);
        return 0;
    }

    // --- Status ---

    private static int CmdStatus(NixApi api)
    {
        var horde = new HordeApi(api);

        Console.WriteLine("AI Horde Status");
        Console.WriteLine("  Endpoint:  " + horde.Endpoint);
        Console.Write("  Reachable: ");

        if (!horde.IsAvailable())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("NO");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("YES");
        Console.ResetColor();

        Console.WriteLine("  API Key:   " + (horde.ApiKey == "0000000000" ? "(anonymous)" : "****" + horde.ApiKey.Substring(Math.Max(0, horde.ApiKey.Length - 4))));

        if (!string.IsNullOrEmpty(horde.Model))
            Console.WriteLine("  Model:     " + horde.Model);
        else
            Console.WriteLine("  Model:     (any available)");

        return 0;
    }

    // --- Models ---

    private static int CmdModels(NixApi api)
    {
        var horde = new HordeApi(api);
        ShowModels(horde);
        return 0;
    }

    private static void ShowModels(HordeApi horde)
    {
        Console.WriteLine("Fetching available text models...");
        var models = horde.GetAvailableModels();
        if (models == null || models.Count == 0)
        {
            Console.WriteLine("  (no models available or request failed)");
            return;
        }

        Console.WriteLine("Available text models (" + models.Count + "):");
        foreach (var m in models)
            Console.WriteLine("  " + m);
    }

    // --- Config ---

    private static int CmdConfig(NixApi api)
    {
        var horde = new HordeApi(api);

        string home = api.Uid == 0 ? "/root" : "/home/" + api.Username;
        string userPath = home + "/.config/aihorde.conf";
        string sysPath = "/etc/opt/aihorde.conf";

        Console.WriteLine("AI Horde Settings");
        Console.WriteLine();
        Console.WriteLine("  Per-user file:    " + userPath + (api.IsFile(userPath) ? "" : " (not created)"));
        Console.WriteLine("  System-wide file: " + sysPath + (api.IsFile(sysPath) ? "" : " (not created)"));
        Console.WriteLine();

        PrintSettingsDetailed(api, horde);

        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  aihorde --set <key> <value>  Change a setting (per-user)");
        Console.WriteLine("  aihorde --reset              Reset all settings to defaults");
        Console.WriteLine();
        Console.WriteLine("Configurable keys:");
        Console.WriteLine("  endpoint       AI Horde API base URL");
        Console.WriteLine("  api_key        Horde API key");
        Console.WriteLine("  max_length     Max generation length (tokens)");
        Console.WriteLine("  max_context    Max context length (tokens)");
        Console.WriteLine("  temperature    Sampling temperature (0.0-2.0)");
        Console.WriteLine("  top_p          Top-p nucleus sampling (0.0-1.0)");
        Console.WriteLine("  top_k          Top-k sampling");
        Console.WriteLine("  rep_penalty    Repetition penalty (1.0 = none)");
        Console.WriteLine("  model          Preferred model (empty = any)");
        Console.WriteLine("  poll_interval  Seconds between status polls");
        Console.WriteLine("  max_wait       Max seconds to wait for generation");
        Console.WriteLine("  system_prompt  System prompt for chat");

        return 0;
    }

    // --- Set ---

    private static int CmdSet(NixApi api, string key, string value)
    {
        var validKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "endpoint", "api_key", "max_length", "max_context", "temperature",
            "top_p", "top_k", "rep_penalty", "model", "poll_interval",
            "max_wait", "system_prompt"
        };

        if (!validKeys.Contains(key))
        {
            Console.WriteLine("aihorde: unknown setting '" + key + "'");
            Console.WriteLine("Run 'aihorde --config' to see valid keys.");
            return 1;
        }

        Settings.Set(api, HordeApi.AppName, key, value);
        Console.WriteLine("aihorde: " + key + " = " + value);
        return 0;
    }

    // --- Helpers ---

    private static void PrintSettings(HordeApi horde)
    {
        Console.WriteLine("  endpoint       = " + horde.Endpoint);
        Console.WriteLine("  api_key        = " + (horde.ApiKey == "0000000000" ? "(anonymous)" : "****" + horde.ApiKey.Substring(Math.Max(0, horde.ApiKey.Length - 4))));
        Console.WriteLine("  max_length     = " + horde.MaxLength);
        Console.WriteLine("  max_context    = " + horde.MaxContext);
        Console.WriteLine("  temperature    = " + horde.Temperature.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("  top_p          = " + horde.TopP.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("  top_k          = " + horde.TopK);
        Console.WriteLine("  rep_penalty    = " + horde.RepPenalty.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("  model          = " + (string.IsNullOrEmpty(horde.Model) ? "(any)" : horde.Model));
        Console.WriteLine("  poll_interval  = " + horde.PollInterval);
        Console.WriteLine("  max_wait       = " + horde.MaxWait);
        Console.WriteLine("  system_prompt  = " + Truncate(horde.SystemPrompt, 60));
    }

    private static void PrintSettingsDetailed(NixApi api, HordeApi horde)
    {
        Console.WriteLine("  Key              Value                                    Source");
        Console.WriteLine("  ---------------  ----------------------------------------  ----------");
        PrintSettingRow(api, "endpoint",      horde.Endpoint);
        PrintSettingRow(api, "api_key",       horde.ApiKey == "0000000000" ? "(anonymous)" : "****" + horde.ApiKey.Substring(Math.Max(0, horde.ApiKey.Length - 4)));
        PrintSettingRow(api, "max_length",    horde.MaxLength.ToString());
        PrintSettingRow(api, "max_context",   horde.MaxContext.ToString());
        PrintSettingRow(api, "temperature",   horde.Temperature.ToString(CultureInfo.InvariantCulture));
        PrintSettingRow(api, "top_p",         horde.TopP.ToString(CultureInfo.InvariantCulture));
        PrintSettingRow(api, "top_k",         horde.TopK.ToString());
        PrintSettingRow(api, "rep_penalty",   horde.RepPenalty.ToString(CultureInfo.InvariantCulture));
        PrintSettingRow(api, "model",         string.IsNullOrEmpty(horde.Model) ? "(any)" : horde.Model);
        PrintSettingRow(api, "poll_interval", horde.PollInterval.ToString());
        PrintSettingRow(api, "max_wait",      horde.MaxWait.ToString());
        PrintSettingRow(api, "system_prompt", Truncate(horde.SystemPrompt, 35));
    }

    private static void PrintSettingRow(NixApi api, string key, string displayValue)
    {
        string source = SettingSource(api, key);
        Console.WriteLine("  " + key.PadRight(15) + displayValue.PadRight(41) + source);
    }

    private static string SettingSource(NixApi api, string key)
    {
        string userVal = Settings.Get(api, HordeApi.AppName, key);
        string sysVal = Settings.GetSystem(api, HordeApi.AppName, key);

        if (userVal != null && sysVal != null)
            return "user (overrides system)";
        if (userVal != null)
            return "user";
        if (sysVal != null)
            return "system";
        return "default";
    }

    private static void HandleInlineSet(HordeApi horde, string args)
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
            case "max_length":    if (int.TryParse(value, out int ml)) horde.MaxLength = ml; break;
            case "max_context":   if (int.TryParse(value, out int mc)) horde.MaxContext = mc; break;
            case "temperature":   if (double.TryParse(value, out double t)) horde.Temperature = t; break;
            case "top_p":         if (double.TryParse(value, out double tp)) horde.TopP = tp; break;
            case "top_k":         if (int.TryParse(value, out int tk)) horde.TopK = tk; break;
            case "rep_penalty":   if (double.TryParse(value, out double rp)) horde.RepPenalty = rp; break;
            case "poll_interval": if (int.TryParse(value, out int pi)) horde.PollInterval = pi; break;
            case "max_wait":      if (int.TryParse(value, out int mw)) horde.MaxWait = mw; break;
            case "system_prompt": horde.SystemPrompt = value; break;
            case "endpoint":      horde.Endpoint = value; break;
            case "api_key":       horde.ApiKey = value; break;
            case "model":         horde.Model = value; break;
            default:
                Console.WriteLine("Unknown setting: " + key);
                return;
        }

        Console.WriteLine("  " + key + " = " + value + " (session only — use 'aihorde --set' to persist)");
    }

    private static void HandleSave(NixApi api, HordeChat chat, string input)
    {
        string path = null;
        if (input.Length > 5)
            path = input.Substring(5).Trim();

        if (string.IsNullOrEmpty(path))
            path = "~/horde-chat.txt";

        if (path.StartsWith("~/"))
            path = (api.Uid == 0 ? "/root" : "/home/" + api.Username) + path.Substring(1);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# AI Horde Chat Log — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
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
            Console.WriteLine("aihorde: failed to save: " + ex.Message);
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
        Console.WriteLine("aihorde — AI chat client for the AI Horde");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  aihorde                    Start interactive chat");
        Console.WriteLine("  aihorde -m \"message\"       Send a single message");
        Console.WriteLine("  aihorde --status           Check API connection");
        Console.WriteLine("  aihorde --config           Show current settings");
        Console.WriteLine("  aihorde --set key value    Change a setting (per-user)");
        Console.WriteLine("  aihorde --reset            Reset all settings to defaults");
        Console.WriteLine("  aihorde --models           List available text models");
        Console.WriteLine("  aihorde --help             Show this help");
        Console.WriteLine();
        Console.WriteLine("Settings (persisted per-user via settingslib):");
        Console.WriteLine("  endpoint       API base URL          (default: https://aihorde.net/api/v2)");
        Console.WriteLine("  api_key        Horde API key         (default: 0000000000 — anonymous)");
        Console.WriteLine("  max_length     Max generation tokens (default: 256)");
        Console.WriteLine("  max_context    Max context tokens    (default: 2048)");
        Console.WriteLine("  temperature    Sampling temperature  (default: 0.7)");
        Console.WriteLine("  top_p          Nucleus sampling      (default: 0.9)");
        Console.WriteLine("  top_k          Top-k sampling        (default: 40)");
        Console.WriteLine("  rep_penalty    Repetition penalty    (default: 1.1)");
        Console.WriteLine("  model          Preferred model       (default: any available)");
        Console.WriteLine("  poll_interval  Poll interval (sec)   (default: 3)");
        Console.WriteLine("  max_wait       Max wait (sec)        (default: 300)");
        Console.WriteLine("  system_prompt  System prompt for AI  (default: built-in)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  aihorde --set api_key your-horde-key-here");
        Console.WriteLine("  aihorde --set model \"koboldcpp/LLaMA-2 7B\"");
        Console.WriteLine("  aihorde -m \"What is the meaning of life?\"");
        Console.WriteLine("  aihorde --models");
        Console.WriteLine("  aihorde --config");
        Console.WriteLine("  aihorde --reset");
        Console.WriteLine("  aihorde");
        Console.WriteLine();
        Console.WriteLine("Interactive commands:");
        Console.WriteLine("  /quit, /exit     End the chat");
        Console.WriteLine("  /clear           Clear conversation history");
        Console.WriteLine("  /models          List available text models");
        Console.WriteLine("  /config          Show settings");
        Console.WriteLine("  /set key value   Change a setting for this session");
        Console.WriteLine("  /save [path]     Save conversation to a file");
        Console.WriteLine("  /help            Show commands");
        Console.WriteLine();
        Console.WriteLine("The AI Horde is a crowdsourced distributed AI service.");
        Console.WriteLine("Register for an API key at: https://aihorde.net/register");
        Console.WriteLine("Anonymous access (key 0000000000) works but is slower.");
        Console.WriteLine();
        Console.WriteLine("On first use, default settings are installed to:");
        Console.WriteLine("  ~/.config/aihorde.conf      (per-user)");
        Console.WriteLine("  /etc/opt/aihorde.conf       (system-wide, if root)");
        Console.WriteLine();
        Console.WriteLine("See also: man aihorde, man koboldlib, man settingslib");
    }
}

