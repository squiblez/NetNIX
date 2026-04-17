namespace NetNIX.Setup;

/// <summary>
/// Loads manual pages from the helpman/ directory that ships alongside
/// the NetNIX binary. These .txt files are plain-text content files
/// copied to the output directory and installed into /usr/share/man/
/// in the virtual filesystem.
///
/// helpman/*.txt ? /usr/share/man/*.txt
///
/// To add or remove man pages, simply drop .txt files into the helpman/
/// directory — no code changes required.
/// </summary>
public static class BuiltinManPages
{
    private static readonly string ManDir =
        Path.Combine(AppContext.BaseDirectory, "helpman");

    /// <summary>
    /// Returns a dictionary mapping VFS install paths
    /// (/usr/share/man/name.txt) to the man page content read from
    /// the on-disk helpman/ directory.
    /// </summary>
    public static Dictionary<string, string> LoadAll()
    {
        var pages = new Dictionary<string, string>();

        if (!Directory.Exists(ManDir))
            return pages;

        foreach (var file in Directory.GetFiles(ManDir, "*.txt"))
        {
            string filename = Path.GetFileName(file);
            string vfsPath = "/usr/share/man/" + filename;
            string content = File.ReadAllText(file);
            pages[vfsPath] = content;
        }

        return pages;
    }

    /// <summary>
    /// Reads a single man page by name (e.g. "ls" reads helpman/ls.txt).
    /// Returns null if the file is not found.
    /// </summary>
    public static string? Load(string name)
    {
        string path = Path.Combine(ManDir, name + ".txt");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
