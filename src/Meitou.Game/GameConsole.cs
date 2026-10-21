using System.Runtime.InteropServices;
using System.Text;

namespace Meitou.Game;

/// <summary>
/// MeitouClient.exe is a windowed (GUI subsystem) program, so starting it opens no console window. Its output always goes to log.txt
/// next to the exe (<see cref="GameLog"/>); run from a terminal it also writes into that terminal. The one question it may ask
/// (the Kenshi folder) opens a console of its own for as long as it takes.
/// </summary>
static partial class GameConsole
{
    const uint AttachParentProcess = unchecked((uint)-1);

    /// <summary>Joins the console of the terminal that started the game, if any. Call before anything is written.</summary>
    public static void AttachToParent()
    {
        if (!OperatingSystem.IsWindows() || GetConsoleWindow() != 0) return;
        if (AttachConsole(AttachParentProcess) != 0) Rebind();
    }

    static TextWriter? log;

    /// <summary>Starts log.txt (after <see cref="AttachToParent"/>): output and error go to it as well, and so does an exception that ends the game.</summary>
    public static void StartLog()
    {
        if (GameLog.Open() is not { } file) return;
        var shared = TextWriter.Synchronized(file);   // one lock for output and error
        log = shared;
        Bind(Console.Out, Console.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => shared.WriteLine($"unhandled {e.ExceptionObject}");
    }

    static void Bind(TextWriter output, TextWriter error)
    {
        Console.SetOut(log is null ? output : new TeeWriter(log, output));
        Console.SetError(log is null ? error : new TeeWriter(log, error));
    }

    /// <summary>A console to ask a question on: the attached one, else a new window that closes again when this is disposed.</summary>
    public static IDisposable ForPrompt()
    {
        if (!OperatingSystem.IsWindows() || GetConsoleWindow() != 0 || AllocConsole() == 0) return new Release(false);
        Rebind();
        return new Release(true);
    }

    // Console's readers and writers are made on first use from the handles the process had then; point them at the new console.
    static void Rebind()
    {
        // Console's own writers never write a byte-order mark; a StreamWriter over UTF-8 would.
        var encoding = Console.OutputEncoding.CodePage == 65001 ? new UTF8Encoding(false) : Console.OutputEncoding;
        Bind(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true }, new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), Console.InputEncoding));
    }

    sealed class Release(bool free) : IDisposable
    {
        public void Dispose()
        {
            if (!free) return;
            free = false;
            Bind(TextWriter.Null, TextWriter.Null);
            Console.SetIn(TextReader.Null);
            FreeConsole();
        }
    }

    [LibraryImport("kernel32.dll")] private static partial nint GetConsoleWindow();
    [LibraryImport("kernel32.dll")] private static partial int AttachConsole(uint processId);
    [LibraryImport("kernel32.dll")] private static partial int AllocConsole();
    [LibraryImport("kernel32.dll")] private static partial int FreeConsole();
}
