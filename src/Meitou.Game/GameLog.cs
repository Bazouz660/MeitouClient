using System.Text;

namespace Meitou.Game;

/// <summary>
/// log.txt next to MeitouClient.exe: everything the game writes to its output and error, appended across runs. Past <see cref="Cap"/> the
/// oldest lines are cut, down to <see cref="Keep"/>. Only one running game writes it; a second one started from the same folder logs nothing.
/// </summary>
sealed class GameLog : TextWriter
{
    const long Cap = 10 << 20, Keep = 8 << 20;

    readonly FileStream file;
    readonly StreamWriter writer;

    GameLog(FileStream file)
    {
        this.file = file;
        writer = new StreamWriter(file, new UTF8Encoding(false)) { AutoFlush = true };   // flushed per write: a crash keeps its last lines
    }

    public override Encoding Encoding => writer.Encoding;

    /// <summary>The log of this run, or null when it cannot be opened (another game has it, or the folder is read-only).</summary>
    public static GameLog? Open()
    {
        try
        {
            var file = new FileStream(Path.Combine(AppContext.BaseDirectory, "log.txt"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            file.Seek(0, SeekOrigin.End);
            var log = new GameLog(file);
            log.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} meitou {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
            return log;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public override void Write(char value) { writer.Write(value); Trim(); }
    public override void Write(string? value) { writer.Write(value); Trim(); }
    public override void Write(char[] buffer, int index, int count) { writer.Write(buffer, index, count); Trim(); }
    public override void WriteLine(string? value) { writer.WriteLine(value); Trim(); }

    void Trim()
    {
        if (file.Length <= Cap) return;
        var tail = new byte[Keep];
        file.Seek(-Keep, SeekOrigin.End);
        file.ReadExactly(tail);
        int start = Array.IndexOf(tail, (byte)'\n') + 1;   // from a whole line
        file.SetLength(0);
        file.Write(tail, start, tail.Length - start);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) writer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Writes to two writers: the log and the console (or wherever the output was redirected).</summary>
sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
{
    public override Encoding Encoding => second.Encoding;
    public override void Write(char value) { first.Write(value); second.Write(value); }
    public override void Write(string? value) { first.Write(value); second.Write(value); }
    public override void Write(char[] buffer, int index, int count) { first.Write(buffer, index, count); second.Write(buffer, index, count); }
    public override void WriteLine(string? value) { first.WriteLine(value); second.WriteLine(value); }
    public override void Flush() { first.Flush(); second.Flush(); }
}
