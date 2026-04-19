using System;
using NetNIX.Scripting;

public static class UnameCommand
{
    public static int Run(NixApi api, string[] args)
    {
        string ver = NixApi.SystemVersion;
        bool all = args.Length > 0 && (args[0] == "-a" || args[0] == "--all");

        if (all)
            Console.WriteLine($"NetNIX {ver} netnix NetNIX-VFS .NET8 nsh");
        else if (args.Length > 0 && args[0] == "-r")
            Console.WriteLine(ver);
        else if (args.Length > 0 && (args[0] == "-v" || args[0] == "--version"))
            Console.WriteLine($"NetNIX v{ver}");
        else if (args.Length > 0 && args[0] == "-s")
            Console.WriteLine("NetNIX");
        else if (args.Length > 0 && args[0] == "-n")
            Console.WriteLine("netnix");
        else
            Console.WriteLine("NetNIX");

        return 0;
    }
}
