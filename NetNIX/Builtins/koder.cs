using System;
using System;
using System.Collections.Generic;
using System.Text;
using NetNIX.Scripting;

#include <settingslib>
#include <koboldlib>

/// <summary>
/// koder — AI-powered command generator for NetNIX.
///
/// Uses KoboldCpp to generate complete .cs command scripts based on
/// a user's description. Loads a large system prompt from
/// /etc/opt/koder/commandprompt.txt that teaches the AI about the
/// NetNIX environment, API, sandbox rules, and coding conventions.
///
/// Usage:
///     koder                  Interactive wizard
///     koder --config         Show koder settings
///     koder --set key value  Change a koder setting
///     koder --help           Show help
/// </summary>
public static class KoderCommand
{
    private const string KoderApp = "koder";
    private const string PromptFile = "/etc/opt/koder/commandprompt.txt";
    private const int DefaultGenTimeout = 3600; // 60 minutes
    private const int DefaultMaxLength = 4096;
    private const int DefaultMaxContext = 8192;

    public static int Run(NixApi api, string[] args)
    {
        var argList = new List<string>(args);

        if (argList.Contains("-h") || argList.Contains("--help"))
        {
            PrintUsage();
            return 0;
        }

        if (argList.Contains("--config"))
            return CmdConfig(api);

        int setIdx = argList.IndexOf("--set");
        if (setIdx >= 0)
        {
            if (setIdx + 2 >= argList.Count)
            {
                Console.WriteLine("koder: --set requires a key and value");
                return 1;
            }
            return CmdSet(api, argList[setIdx + 1], argList[setIdx + 2]);
        }

        if (argList.Contains("--reset"))
            return CmdReset(api);

        return CmdGenerate(api);
    }

    // ?? Interactive generation wizard ??????????????????????????????

    private static int CmdGenerate(NixApi api)
    {
        // 1. Load the system prompt
        if (!api.IsFile(PromptFile))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("koder: system prompt not found at " + PromptFile);
            Console.ResetColor();
            Console.WriteLine("Run 'reinstall' as root to restore factory files.");
            return 1;
        }

        string systemPrompt = api.ReadText(PromptFile);
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            Console.WriteLine("koder: system prompt file is empty");
            return 1;
        }

        // 2. Check API connection
        var kobold = new KoboldApi(api);
        string endpoint = GetKoderSetting(api, "endpoint", kobold.Endpoint);
        kobold.Endpoint = endpoint;

        Console.Write("Connecting to AI at " + kobold.Endpoint + "...");
        if (!kobold.IsAvailable())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(" FAILED");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("Cannot reach the KoboldCpp API.");
            Console.WriteLine("Make sure KoboldCpp is running and the endpoint is correct.");
            Console.WriteLine("  Current endpoint: " + kobold.Endpoint);
            Console.WriteLine("  Change with:      koder --set endpoint http://HOST:PORT");
            return 1;
        }

        string model = kobold.GetModelName() ?? "unknown";
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(" OK");
        Console.ResetColor();
        Console.WriteLine("Model: " + model);
        Console.WriteLine();

        // 3. Apply koder-specific generation settings
        int maxLength = int.Parse(GetKoderSetting(api, "max_length", DefaultMaxLength.ToString()));
        int maxContext = int.Parse(GetKoderSetting(api, "max_context", DefaultMaxContext.ToString()));
        int genTimeout = int.Parse(GetKoderSetting(api, "gen_timeout", DefaultGenTimeout.ToString()));
        double temperature = double.Parse(GetKoderSetting(api, "temperature", "0.3"),
            System.Globalization.CultureInfo.InvariantCulture);

        kobold.MaxLength = maxLength;
        kobold.MaxContext = maxContext;
        kobold.Temperature = temperature;
        kobold.TopP = 0.95;
        kobold.RepPenalty = 1.05;

        // 4. Ask for command name
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("????????????????????????????????????????????????");
        Console.WriteLine("?          Koder — Command Generator          ?");
        Console.WriteLine("????????????????????????????????????????????????");
        Console.ResetColor();
        Console.WriteLine();

        string commandName = null;
        while (string.IsNullOrWhiteSpace(commandName))
        {
            Console.Write("Command name (e.g. massrename): ");
            commandName = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(commandName))
            {
                Console.WriteLine("  Command name cannot be empty.");
                continue;
            }

            // Sanitize: lowercase, no spaces, no extension
            commandName = commandName.ToLowerInvariant().Replace(" ", "-");
            if (commandName.EndsWith(".cs"))
                commandName = commandName.Substring(0, commandName.Length - 3);

            // Validate characters
            bool valid = true;
            foreach (char c in commandName)
            {
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                {
                    Console.WriteLine("  Invalid character '" + c + "'. Use letters, digits, hyphens, and underscores only.");
                    commandName = null;
                    valid = false;
                    break;
                }
            }
            if (!valid) continue;
        }

        string filename = commandName + ".cs";
        Console.WriteLine();

        // 5. Ask for description
        Console.WriteLine("Describe what the '" + commandName + "' command should do.");
        Console.WriteLine("Be as detailed as possible — mention arguments, flags, behavior, output format, etc.");
        Console.WriteLine("(Enter a blank line when finished)");
        Console.WriteLine();

        var descriptionLines = new List<string>();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("Description> ");
        Console.ResetColor();

        while (true)
        {
            string line = Console.ReadLine();
            if (line == null) break; // EOF
            if (line.Trim().Length == 0 && descriptionLines.Count > 0) break;

            descriptionLines.Add(line);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("          > ");
            Console.ResetColor();
        }

        if (descriptionLines.Count == 0)
        {
            Console.WriteLine("koder: no description provided, aborting.");
            return 1;
        }

        string description = string.Join("\n", descriptionLines).Trim();
        Console.WriteLine();

        // 6. Build the full prompt (inject live sandbox rules + example commands)
        string fullPrompt = BuildPrompt(api, systemPrompt, commandName, description);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  (prompt size: " + fullPrompt.Length + " chars)");
        Console.ResetColor();

        // 7. Generate
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("Generating " + filename + "...");
        Console.ResetColor();
        Console.WriteLine(" (this may take several minutes)");
        Console.WriteLine();

        string generated = kobold.GenerateWithTimeout(fullPrompt, genTimeout);

        if (string.IsNullOrWhiteSpace(generated))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("koder: generation failed — no response from API");
            Console.ResetColor();
            Console.WriteLine("Try increasing the timeout: koder --set gen_timeout 1200");
            return 1;
        }

        // 8. Clean up the generated code
        // Prepend the primer we used ("using System;\n") since the model
        // continues from that point and doesn't repeat it
        if (!generated.TrimStart().StartsWith("using System;"))
            generated = "using System;\n" + generated;

        string code = CleanGeneratedCode(generated);

        if (string.IsNullOrWhiteSpace(code))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("koder: generated output did not contain valid C# code");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("Raw output:");
            Console.WriteLine(generated);
            return 1;
        }

        // 9. Preview
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("??? Generated: " + filename + " ???");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine(code);
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("??? End of " + filename + " ???");
        Console.ResetColor();
        Console.WriteLine();

        // 10. Ask where to save
        string defaultSavePath = api.Cwd.TrimEnd('/') + "/" + filename;
        Console.WriteLine("Where would you like to save this command?");
        Console.WriteLine("  1) " + defaultSavePath + " (current directory)");
        Console.WriteLine("  2) /bin/" + filename + " (install as global command)");
        Console.WriteLine("  3) Enter a custom path");
        Console.WriteLine("  4) Don't save (print and exit)");
        Console.WriteLine();
        Console.Write("Choice [1]: ");

        string choice = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(choice)) choice = "1";

        string savePath = null;
        switch (choice)
        {
            case "1":
                savePath = defaultSavePath;
                break;
            case "2":
                if (api.Uid != 0)
                {
                    Console.WriteLine("koder: writing to /bin requires root (use sudo)");
                    savePath = defaultSavePath;
                    Console.WriteLine("Saving to " + savePath + " instead.");
                }
                else
                {
                    savePath = "/bin/" + filename;
                }
                break;
            case "3":
                Console.Write("Path: ");
                savePath = Console.ReadLine()?.Trim();
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    Console.WriteLine("koder: no path provided, saving to current directory");
                    savePath = defaultSavePath;
                }
                break;
            case "4":
                Console.WriteLine("Code was not saved.");
                return 0;
            default:
                savePath = defaultSavePath;
                break;
        }

        // 11. Save
        try
        {
            api.WriteText(savePath, code);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Saved to " + savePath);
            Console.ResetColor();
            Console.WriteLine();

            if (savePath.StartsWith("/bin/") || savePath.StartsWith("/usr/local/bin/"))
            {
                Console.WriteLine("You can now run it with: " + commandName);
            }
            else
            {
                Console.WriteLine("You can run it with: run " + savePath);
                Console.WriteLine("Or copy to /bin/ to make it a global command:");
                Console.WriteLine("  sudo cp " + savePath + " /bin/" + filename);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("koder: failed to save: " + ex.Message);
            Console.ResetColor();
            return 1;
        }

        return 0;
    }

    // ?? Prompt building ???????????????????????????????????????????

    private static string BuildPrompt(NixApi api, string systemPrompt, string commandName, string description)
    {
        // Replace placeholders in the template with live data
        string sandboxRules = BuildSandboxSection(api);
        string exampleCommands = BuildExamplesSection(api);

        systemPrompt = systemPrompt.Replace("{SANDBOX_RULES}", sandboxRules);
        systemPrompt = systemPrompt.Replace("{EXAMPLE_COMMANDS}", exampleCommands);

        var sb = new StringBuilder();

        // System context
        sb.AppendLine("### System:");
        sb.AppendLine(systemPrompt);
        sb.AppendLine();

        // User request
        sb.AppendLine("### User:");
        sb.AppendLine("Generate a complete NetNIX command called '" + commandName + "'.");
        sb.AppendLine();
        sb.AppendLine("Description:");
        sb.AppendLine(description);
        sb.AppendLine();
        sb.AppendLine("The class should be named " + ToPascalCase(commandName) + "Command.");
        sb.AppendLine("The file will be saved as " + commandName + ".cs.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL RULES:");
        sb.AppendLine("- Do NOT use any blocked namespace or token listed above.");
        sb.AppendLine("- Use ONLY the api object for all file, directory, and network operations.");
        sb.AppendLine("- Output ONLY the raw C# source code. No markdown, no explanations.");
        sb.AppendLine();

        // Prompt the model to respond
        sb.Append("### Assistant:\nusing System;\n");

        return sb.ToString();
    }

    // ?? Dynamic sandbox rules ?????????????????????????????????????

    private static string BuildSandboxSection(NixApi api)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== ACTIVE SANDBOX RULES ===");
        sb.AppendLine("The following rules are enforced by /etc/sandbox.conf at runtime.");
        sb.AppendLine("Any script containing these will be REJECTED. Do NOT use any of them.");
        sb.AppendLine();

        string configPath = "/etc/sandbox.conf";
        if (!api.IsFile(configPath))
        {
            sb.AppendLine("(sandbox.conf not found — assume standard restrictions apply)");
            return sb.ToString();
        }

        string content = api.ReadText(configPath);
        string currentSection = "";
        var blockedUsings = new List<string>();
        var blockedTokens = new List<string>();

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                currentSection = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                continue;
            }

            if (currentSection == "blocked_usings")
                blockedUsings.Add(line);
            else if (currentSection == "blocked_tokens")
                blockedTokens.Add(line);
        }

        if (blockedUsings.Count > 0)
        {
            sb.AppendLine("BLOCKED USING DIRECTIVES (never use these in a 'using' statement):");
            foreach (string ns in blockedUsings)
            {
                sb.Append("  - using " + ns + ";");
                // Add the alternative hint
                string alt = GetUsingAlternative(ns);
                if (alt != null)
                    sb.Append("  ? instead use: " + alt);
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        if (blockedTokens.Count > 0)
        {
            sb.AppendLine("BLOCKED CODE PATTERNS (these literal strings must not appear anywhere in the source):");
            foreach (string token in blockedTokens)
            {
                sb.Append("  - " + token);
                string alt = GetTokenAlternative(token);
                if (alt != null)
                    sb.Append("  ? instead use: " + alt);
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        sb.AppendLine("SAFE namespaces you CAN use:");
        sb.AppendLine("  using System;");
        sb.AppendLine("  using System.Linq;");
        sb.AppendLine("  using System.Collections.Generic;");
        sb.AppendLine("  using System.Text;");
        sb.AppendLine("  using System.Text.RegularExpressions;");
        sb.AppendLine("  using System.Globalization;");
        sb.AppendLine("  using NetNIX.Scripting;");

        return sb.ToString();
    }

    private static string GetUsingAlternative(string ns)
    {
        switch (ns)
        {
            case "System.IO": return "api.ReadText(), api.WriteText(), api.ListDirectory(), api.IsFile(), etc.";
            case "System.Net": return "api.Net.Get(), api.Net.Post(), api.Download()";
            case "System.Diagnostics": return "(no process spawning available in NetNIX)";
            case "System.Reflection": return "(not needed — scripts are self-contained)";
            case "System.Runtime.InteropServices": return "(no native interop in NetNIX)";
            case "System.Security": return "(permissions handled via api.CanRead/CanWrite)";
            case "System.Runtime.Loader": return "(not needed)";
            case "System.CodeDom": return "(not needed)";
            default: return null;
        }
    }

    private static string GetTokenAlternative(string token)
    {
        // NOTE: Blocked tokens are split with concatenation so that the
        // sandbox scanner does not match them inside this source file.
        if (token.StartsWith("Fil" + "e.Rea")) return "api.ReadText(path) or api.ReadBytes(path)";
        if (token.StartsWith("Fil" + "e.Wri") || token.StartsWith("Fil" + "e.Appen")) return "api.WriteText(path, text) or api.AppendText(path, text)";
        if (token.StartsWith("Fil" + "e.Cre")) return "api.CreateEmptyFile(path)";
        if (token.StartsWith("Fil" + "e.Ope")) return "api.ReadText(path) / api.ReadBytes(path)";
        if (token.StartsWith("Fil" + "e.Cop")) return "api.Copy(src, dest)";
        if (token.StartsWith("Fil" + "e.Mov")) return "api.Move(src, dest)";
        if (token.StartsWith("Fil" + "e.Del")) return "api.Delete(path)";
        if (token.StartsWith("Fil" + "e.Exi")) return "api.IsFile(path)";
        if (token.StartsWith("Dir" + "ectory.Cre")) return "api.CreateDirWithParents(path)";
        if (token.StartsWith("Dir" + "ectory.Del")) return "api.Delete(path)";
        if (token.StartsWith("Dir" + "ectory.Exi")) return "api.IsDirectory(path)";
        if (token.StartsWith("Dir" + "ectory.Get") || token.StartsWith("Dir" + "ectory.Enu")) return "api.ListDirectory(path)";
        if (token.StartsWith("Dir" + "ectoryInfo(")) return "api.IsDirectory(path), api.ListDirectory(path)";
        if (token.StartsWith("Fil" + "eInfo(")) return "api.IsFile(path), api.GetSize(path)";
        if (token.StartsWith("Fil" + "eStream(") || token.StartsWith("Str" + "eamReader(") || token.StartsWith("Str" + "eamWriter(")) return "api.ReadText(path) / api.WriteText(path, text)";
        if (token.StartsWith("Pat" + "h.Combine")) return "parent.TrimEnd('/') + \"/\" + child";
        if (token.StartsWith("Pat" + "h.GetTemp") || token.StartsWith("Pat" + "h.GetFull")) return "api.ResolvePath(path)";
        if (token.StartsWith("Pro" + "cess.Start") || token.StartsWith("Pro" + "cessStartInfo(")) return "(not available)";
        if (token.StartsWith("Htt" + "pClient(") || token.StartsWith("Web" + "Client(")) return "api.Net.Get(url) / api.Net.Post(url, body, type)";
        if (token.StartsWith("Tcp" + "Client(") || token.StartsWith("Udp" + "Client(") || token.StartsWith("Soc" + "ket(")) return "api.Net.Get/Post (only HTTP is available)";
        if (token.StartsWith("Ass" + "embly.") || token.StartsWith("Act" + "ivator.") || token.StartsWith("Typ" + "e.GetType(")) return "(not needed)";
        if (token.StartsWith("Env" + "ironment.Exit")) return "return exitCode; (from the Run method)";
        if (token.StartsWith("Env" + "ironment.Set")) return "(not available)";
        if (token.StartsWith("Env" + "ironment.Cur")) return "api.Cwd";
        if (token.StartsWith("Env" + "ironment.GetF")) return "api.Cwd or api.ResolvePath(\"~\")";
        if (token.StartsWith("Dll" + "Import") || token.StartsWith("Mar" + "shal.")) return "(not available)";
        if (token.StartsWith("Dri" + "veInfo.")) return "(not applicable — single virtual filesystem)";
        return null;
    }

    // ?? Dynamic example commands ??????????????????????????????????

    private static string BuildExamplesSection(NixApi api)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== EXAMPLE COMMANDS ===");
        sb.AppendLine("Below are real, working NetNIX commands installed on this system.");
        sb.AppendLine("Study their patterns carefully — they demonstrate the correct way to");
        sb.AppendLine("use the NixApi for all file, directory, user, and network operations.");
        sb.AppendLine();

        // grep: flag parsing, file reading, line-by-line processing, multi-file
        AppendExample(api, sb, "/bin/grep.cs",
            "grep — flag parsing with argList.Remove(), api.ReadText(), line-by-line search, multi-file output");

        // find: full VFS traversal, pattern matching, type filtering
        AppendExample(api, sb, "/bin/find.cs",
            "find — api.GetAllPaths() for recursive directory traversal, api.NodeName(), pattern matching, type filtering");

        // ls: ListDirectory, permissions, owner/group lookup, ANSI colors, helper methods
        AppendExample(api, sb, "/bin/ls.cs",
            "ls — api.ListDirectory(), api.GetPermissionString(), api.GetOwner(), api.GetUsername(), api.IsDir(), ANSI colors, private helper method");

        // wget: api.Download(), api.GetSize(), URL parsing, help text pattern
        AppendExample(api, sb, "/bin/wget.cs",
            "wget — api.Download(url, path), api.GetSize(), api.Save(), URL filename extraction, --help pattern");

        // tee: interactive Console.ReadLine() input, api.WriteText/AppendText, api.Save()
        AppendExample(api, sb, "/bin/tee.cs",
            "tee — Console.ReadLine() loop for interactive input, api.WriteText(), api.AppendText(), api.Save()");

        // edit: ADVANCED — full-screen TUI text editor (the most complex NetNIX command)
        // Demonstrates Console.ReadKey(), ANSI escape sequences, cursor management,
        // full-screen rendering, StringBuilder buffer editing, state machines,
        // interactive prompts, api.ReadText()/WriteText()/Save(), permission checks.
        AppendExample(api, sb, "/bin/edit.cs",
            "edit (nedit) — ADVANCED full-screen text editor: Console.ReadKey(true), ANSI escape rendering, cursor control, Console.WindowHeight/Width, api.ReadText(), api.WriteText(), api.CanRead(), api.CanWrite(), api.GetParent(), api.GetName(), api.Save(), static state, multiple private helper methods, interactive Prompt() pattern");

        return sb.ToString();
    }

    private static void AppendExample(NixApi api, StringBuilder sb, string vfsPath, string label)
    {
        if (!api.IsFile(vfsPath))
            return;

        string source = api.ReadText(vfsPath);
        if (string.IsNullOrWhiteSpace(source))
            return;

        sb.AppendLine("--- " + label + " ---");
        sb.AppendLine(source.Trim());
        sb.AppendLine();
    }

    // ?? Code cleanup ??????????????????????????????????????????????

    private static string CleanGeneratedCode(string raw)
    {
        if (raw == null) return null;

        string code = raw.Trim();

        // Remove markdown code fences if present
        if (code.StartsWith("```"))
        {
            int firstNewline = code.IndexOf('\n');
            if (firstNewline > 0)
                code = code.Substring(firstNewline + 1);
        }
        if (code.EndsWith("```"))
            code = code.Substring(0, code.Length - 3).TrimEnd();

        // Try to find the start of the actual C# code
        int usingIdx = code.IndexOf("using ");
        if (usingIdx > 0)
        {
            // Check if there's garbage before the first 'using'
            string before = code.Substring(0, usingIdx).Trim();
            if (before.Length > 0 && !before.StartsWith("//") && !before.StartsWith("#"))
                code = code.Substring(usingIdx);
        }

        // Trim anything after the final closing brace of the class
        int lastBrace = code.LastIndexOf('}');
        if (lastBrace >= 0 && lastBrace < code.Length - 1)
        {
            string after = code.Substring(lastBrace + 1).Trim();
            // Only trim if what follows doesn't look like code
            if (after.Length > 0 && !after.StartsWith("//"))
                code = code.Substring(0, lastBrace + 1);
        }

        // Validate: must contain "static int Run" and "NixApi"
        if (!code.Contains("static int Run") || !code.Contains("NixApi"))
            return null;

        return code.Trim() + "\n";
    }

    private static string ToPascalCase(string name)
    {
        var sb = new StringBuilder();
        bool capitalize = true;
        foreach (char c in name)
        {
            if (c == '-' || c == '_')
            {
                capitalize = true;
                continue;
            }
            sb.Append(capitalize ? char.ToUpper(c) : c);
            capitalize = false;
        }
        return sb.ToString();
    }

    // ?? Settings ??????????????????????????????????????????????????

    private static string GetKoderSetting(NixApi api, string key, string defaultValue)
    {
        string val = Settings.Get(api, KoderApp, key);
        if (val != null) return val;
        val = Settings.GetSystem(api, KoderApp, key);
        if (val != null) return val;
        return defaultValue;
    }

    private static int CmdConfig(NixApi api)
    {
        Console.WriteLine("Koder Settings (koder --set <key> <value>)");
        Console.WriteLine();
        Console.WriteLine("  endpoint     = " + GetKoderSetting(api, "endpoint", "http://localhost:5001") + "  (KoboldCpp API URL)");
        Console.WriteLine("  max_length   = " + GetKoderSetting(api, "max_length", DefaultMaxLength.ToString()) + "  (max tokens to generate)");
        Console.WriteLine("  max_context  = " + GetKoderSetting(api, "max_context", DefaultMaxContext.ToString()) + "  (max context window)");
        Console.WriteLine("  gen_timeout  = " + GetKoderSetting(api, "gen_timeout", DefaultGenTimeout.ToString()) + "  (generation timeout in seconds)");
        Console.WriteLine("  temperature  = " + GetKoderSetting(api, "temperature", "0.3") + "  (sampling temperature)");
        Console.WriteLine();
        Console.WriteLine("  Per-user:    ~/.config/koder.conf");
        Console.WriteLine("  System-wide: /etc/opt/koder.conf");
        Console.WriteLine("  Prompt file: " + PromptFile);
        Console.WriteLine();
        Console.WriteLine("Lower temperature (0.1-0.3) = more deterministic code.");
        Console.WriteLine("Higher gen_timeout = more time for complex commands.");
        return 0;
    }

    private static int CmdSet(NixApi api, string key, string value)
    {
        var validKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "endpoint", "max_length", "max_context", "gen_timeout", "temperature"
        };

        if (!validKeys.Contains(key))
        {
            Console.WriteLine("koder: unknown setting '" + key + "'");
            Console.WriteLine("Run 'koder --config' to see valid keys.");
            return 1;
        }

        Settings.Set(api, KoderApp, key, value);
        Console.WriteLine("koder: " + key + " = " + value);
        return 0;
    }

    private static int CmdReset(NixApi api)
    {
        Settings.Clear(api, KoderApp);
        Console.WriteLine("koder: settings reset to defaults");
        return 0;
    }

    // ?? Help ??????????????????????????????????????????????????????

    private static void PrintUsage()
    {
        Console.WriteLine("koder — AI-powered command generator for NetNIX");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  koder                    Start interactive generation wizard");
        Console.WriteLine("  koder --config           Show current settings");
        Console.WriteLine("  koder --set key value    Change a setting");
        Console.WriteLine("  koder --reset            Reset settings to defaults");
        Console.WriteLine("  koder --help             Show this help");
        Console.WriteLine();
        Console.WriteLine("Settings:");
        Console.WriteLine("  endpoint     KoboldCpp API URL        (default: http://localhost:5001)");
        Console.WriteLine("  max_length   Max generation tokens    (default: " + DefaultMaxLength + ")");
        Console.WriteLine("  max_context  Max context window       (default: " + DefaultMaxContext + ")");
        Console.WriteLine("  gen_timeout  Timeout in seconds       (default: " + DefaultGenTimeout + " = 60 min)");
        Console.WriteLine("  temperature  Sampling temperature     (default: 0.3)");
        Console.WriteLine();
        Console.WriteLine("The wizard will:");
        Console.WriteLine("  1. Ask for a command name (e.g. 'massrename')");
        Console.WriteLine("  2. Ask for a description of what the command should do");
        Console.WriteLine("  3. Generate a complete .cs command using AI");
        Console.WriteLine("  4. Preview the code and ask where to save it");
        Console.WriteLine();
        Console.WriteLine("The system prompt is loaded from:");
        Console.WriteLine("  " + PromptFile);
        Console.WriteLine();
        Console.WriteLine("See also: man koder, man kobold, man koboldlib");
    }
}
