using System;
using NetNIX.Scripting;

public static class CatCommand
{
    public static int Run(NixApi api, string[] args)
    {
        bool number = false;
        var files = new System.Collections.Generic.List<string>();
        foreach (var a in args)
        {
            if (a == "-n") number = true;
            else files.Add(a);
        }

        if (files.Count == 0)
        {
            // No file argument - read from stdin if it's piped, else error.
            if (Console.IsInputRedirected || NetNIX.Shell.NixShell.IsPiped)
            {
                string text = Console.In.ReadToEnd();
                if (number)
                {
                    var lines = text.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                        Console.WriteLine($"  {i + 1}\t{lines[i]}");
                }
                else
                {
                    Console.Write(text);
                }
                return 0;
            }
            Console.WriteLine("cat: missing operand");
            return 1;
        }

        foreach (var file in files)
        {
            if (!api.Exists(file))
            {
                Console.WriteLine($"cat: {file}: No such file or directory");
                continue;
            }
            if (api.IsDirectory(file))
            {
                Console.WriteLine($"cat: {file}: Is a directory");
                continue;
            }
            if (!api.CanRead(file))
            {
                Console.WriteLine($"cat: {file}: Permission denied");
                continue;
            }
            string text = api.ReadText(file);
            if (number)
            {
                var lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    Console.WriteLine($"  {i + 1}\t{lines[i]}");
            }
            else
            {
                Console.Write(text);
            }
        }
        return 0;
    }
}
