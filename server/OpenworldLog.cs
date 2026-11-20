namespace OpenworldAddon.Server;

/// <summary>
/// The add-on's own log file, next to the server mod: VagabondOpenworld.server.<date>_<time>.log (a new file for every server start). It is written only while
/// a file named VagabondOpenworld.debug exists in the same folder (create it to switch the log on, delete it
/// to switch it off; no restart needed). The normal console lines are printed either way.
/// </summary>
public static class OpenworldLog
{
    private static readonly object Gate = new();
    private static readonly string Dir = FindDir();
    private static readonly string Name = "VagabondOpenworld.server." + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log";
    private static bool _started;
    private static bool _announced;

    private static string FindDir()
    {
        var location = typeof(OpenworldLog).Assembly.Location;
        return string.IsNullOrEmpty(location) ? AppContext.BaseDirectory : Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
    }

    /// <summary>Switched on by the client when its own log is on (route /vagabond-openworld/log).</summary>
    public static bool FromClient { get; set; }

    public static string Folder => Dir;

    public static string FileName => Name;

    public static bool Enabled
    {
        get
        {
            var on = FromClient || File.Exists(Path.Combine(Dir, "VagabondOpenworld.debug"));
            if (!_announced)
            {
                _announced = true;
                Console.WriteLine("[VagabondOpenworld] Log folder: " + Dir + " (" + (on ? "log is on" : "log is off, create VagabondOpenworld.debug there to switch it on") + ")");
            }

            return on;
        }
    }

    /// <summary>A line for the console and, if switched on, for the file.</summary>
    public static void Info(string text)
    {
        Console.WriteLine("[VagabondOpenworld] " + text);
        Trace(text);
    }

    /// <summary>A detail line, only for the file.</summary>
    public static void Trace(string text)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                var path = Path.Combine(Dir, Name);
                if (!_started)
                {
                    _started = true;
                    File.WriteAllText(path, "VagabondOpenworld server log, started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
                }

                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // a log must never break the game
        }
    }

    public static void Error(string where, Exception e)
    {
        Console.WriteLine("[VagabondOpenworld] " + where + " failed: " + e.GetType().Name + ": " + e.Message);
        Trace(where + " failed: " + e);
    }
}
