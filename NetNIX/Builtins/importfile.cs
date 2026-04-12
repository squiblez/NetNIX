using System;
using System.Linq;
using NetNIX.Scripting;

public static class ImportFileCommand
{
    public static int Run(NixApi api, string[] args)
    {
        var argList = args.ToList();

        if (argList.Count == 0 || argList.Remove("-h") || argList.Remove("--help"))
        {
            PrintUsage();
            return argList.Count == 0 ? 1 : 0;
        }

        if (api.Uid != 0)
        {
            Console.WriteLine("importfile: permission denied (must be root)");
            return 1;
        }

        string hostPath = argList[0];

        // Determine VFS destination
        string vfsPath;
        if (argList.Count > 1)
        {
            vfsPath = argList[1];
            // If the destination is an existing directory, place the file inside it
            if (api.IsDirectory(vfsPath))
            {
                string fileName = HostFileName(hostPath);
                vfsPath = vfsPath.TrimEnd('/') + "/" + fileName;
            }
        }
        else
        {
            // Default: current working directory + host filename
            string fileName = HostFileName(hostPath);
            vfsPath = fileName;
        }

        if (api.ImportFile(hostPath, vfsPath))
        {
            int size = api.GetSize(vfsPath);
            Console.WriteLine($"importfile: imported {hostPath} -> {api.ResolvePath(vfsPath)} ({size} bytes)");
            api.Save();
            return 0;
        }

        return 1;
    }

    /// <summary>
    /// Extract the filename from a host path (handles both / and \ separators).
    /// </summary>
    private static string HostFileName(string hostPath)
    {
        int lastSep = hostPath.LastIndexOfAny(['/', '\\']);
        return lastSep >= 0 ? hostPath[(lastSep + 1)..] : hostPath;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("importfile - import a file from the host filesystem into the VFS");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  importfile <host-path> [vfs-path]");
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  <host-path>   Full path to a file on the host OS");
        Console.WriteLine("  [vfs-path]    Destination in the VFS (default: current directory)");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -h, --help    Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  importfile C:\\Users\\me\\data.zip");
        Console.WriteLine("  importfile C:\\Users\\me\\data.zip /tmp/data.zip");
        Console.WriteLine("  importfile C:\\Users\\me\\notes.txt /home/alice/");
        Console.WriteLine();
        Console.WriteLine("Notes:");
        Console.WriteLine("  Only root can import files from the host.");
        Console.WriteLine("  The host file is not modified (it is copied).");
        Console.WriteLine("  If the VFS destination exists it is overwritten.");
    }
}
