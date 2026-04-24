using System;
using System.Linq;
using NetNIX.Scripting;
using NetNIX.Setup;

public static class ReinstallCommand
{
    public static int Run(NixApi api, string[] args)
    {
        var argList = args.ToList();

        if (argList.Remove("-h") || argList.Remove("--help"))
        {
            PrintUsage();
            return 0;
        }

        if (api.Uid != 0)
        {
            Console.WriteLine("reinstall: permission denied (must be root)");
            return 1;
        }

        bool skipFactory = argList.Remove("--no-factory") || argList.Remove("--skip-factory");
        bool askFactory  = argList.Remove("--ask-factory");
        bool keepFactory = argList.Remove("--keep-factory");

        if (argList.Count > 0)
        {
            Console.WriteLine($"reinstall: unknown option: {argList[0]}");
            PrintUsage();
            return 1;
        }

        // Mutually exclusive policy flags.
        int chosen = (askFactory ? 1 : 0) + (keepFactory ? 1 : 0);
        if (skipFactory && chosen > 0)
        {
            Console.Error.WriteLine("reinstall: --no-factory cannot be combined with --ask-factory or --keep-factory");
            return 1;
        }
        if (chosen > 1)
        {
            Console.Error.WriteLine("reinstall: --ask-factory and --keep-factory are mutually exclusive");
            return 1;
        }

        var policy = FirstRunSetup.FactoryOverwritePolicy.Always;
        string policyDesc = "overwriting all factory configs";
        if (askFactory)  { policy = FirstRunSetup.FactoryOverwritePolicy.Ask;   policyDesc = "prompting for each existing factory config"; }
        if (keepFactory) { policy = FirstRunSetup.FactoryOverwritePolicy.Never; policyDesc = "keeping any existing factory configs"; }

        if (skipFactory)
        {
            Console.WriteLine("Reinstalling factory binaries, libraries, and man pages (skipping factory config files)...");
        }
        else
        {
            Console.WriteLine($"Reinstalling factory binaries, libraries, man pages, and config files ({policyDesc})...");
        }

        if (api.ReinstallFactory(!skipFactory, policy))
        {
            Console.WriteLine("reinstall: factory files restored successfully");
            return 0;
        }

        Console.WriteLine("reinstall: failed");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("reinstall - restore factory binaries, libraries, and man pages");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  reinstall [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --ask-factory     Prompt y/n/a/q before overwriting each existing");
        Console.WriteLine("                    factory config (e.g. files in /etc).");
        Console.WriteLine("  --keep-factory    Skip any factory config that already exists");
        Console.WriteLine("                    (preserves your local edits).");
        Console.WriteLine("  --no-factory      Skip the factory-files step entirely.");
        Console.WriteLine("  --skip-factory    Same as --no-factory.");
        Console.WriteLine("  -h, --help        Show this help.");
        Console.WriteLine();
        Console.WriteLine("Description:");
        Console.WriteLine("  Overwrites /bin, /sbin, /lib, and /usr/share/man with the");
        Console.WriteLine("  factory versions shipped with the NetNIX executable.");
        Console.WriteLine();
        Console.WriteLine("  By default, factory configuration files in /etc are ALSO");
        Console.WriteLine("  overwritten with the factory versions. Use --keep-factory to");
        Console.WriteLine("  preserve them, --ask-factory to be prompted per file, or");
        Console.WriteLine("  --no-factory to skip the entire factory-files step.");
        Console.WriteLine();
        Console.WriteLine("  This is useful after an update to restore new or fixed commands,");
        Console.WriteLine("  or if built-in files have been accidentally modified or deleted.");
        Console.WriteLine();
        Console.WriteLine("  User files, home directories, and settings are NOT affected.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  reinstall                  Full reinstall (overwrites /etc configs)");
        Console.WriteLine("  reinstall --ask-factory    Ask y/n/a/q for each existing config");
        Console.WriteLine("  reinstall --keep-factory   Reinstall but keep your /etc edits");
        Console.WriteLine("  reinstall --no-factory     Reinstall binaries only, no factory files");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine("  Only root can run this command.");
        Console.WriteLine("  See 'man reinstall' for full documentation.");
    }
}
