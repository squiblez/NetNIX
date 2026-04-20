using System;
using System.Linq;
using NetNIX.Scripting;

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

        if (argList.Count > 0)
        {
            Console.WriteLine($"reinstall: unknown option: {argList[0]}");
            PrintUsage();
            return 1;
        }

        if (skipFactory)
        {
            Console.WriteLine("Reinstalling factory binaries, libraries, and man pages (skipping factory config files)...");
        }
        else
        {
            Console.WriteLine("Reinstalling factory binaries, libraries, man pages, and config files...");
        }

        if (api.ReinstallFactory(!skipFactory))
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
        Console.WriteLine("  --no-factory      Skip reinstalling factory config files in /etc");
        Console.WriteLine("  --skip-factory    (same as --no-factory)");
        Console.WriteLine("  -h, --help        Show this help");
        Console.WriteLine();
        Console.WriteLine("Description:");
        Console.WriteLine("  Overwrites /bin, /sbin, /lib, and /usr/share/man with the");
        Console.WriteLine("  factory versions shipped with the NetNIX executable.");
        Console.WriteLine();
        Console.WriteLine("  By default, factory configuration files in /etc are also restored.");
        Console.WriteLine("  Use --no-factory to preserve your existing /etc config files.");
        Console.WriteLine();
        Console.WriteLine("  This is useful after an update to restore new or fixed commands,");
        Console.WriteLine("  or if built-in files have been accidentally modified or deleted.");
        Console.WriteLine();
        Console.WriteLine("  User files, home directories, and settings are NOT affected.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  reinstall                 Full reinstall (binaries + config)");
        Console.WriteLine("  reinstall --no-factory    Reinstall binaries only, keep /etc files");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine("  Only root can run this command.");
        Console.WriteLine("  See 'man reinstall' for full documentation.");
    }
}
