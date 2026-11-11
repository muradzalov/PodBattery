namespace PodBattery;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\PodBattery", out var isFirstInstance);
        if (!isFirstInstance)
            return;

        // A tray app should never pop up error dialogs: log unexpected errors and keep running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(forceLog: args.Contains("--log", StringComparer.OrdinalIgnoreCase)));
    }
}

/// <summary>Appends unexpected errors to %APPDATA%\PodBattery\errors.log (kept small).</summary>
internal static class ErrorLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly Lock Gate = new();

    public static void Write(Exception? error)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Folder);
                var path = Path.Combine(Settings.Folder, "errors.log");
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {error}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
