// UiTextWriter.cs

using System.Text;

namespace WebSnapshots;

public sealed class UiTextWriter : TextWriter
{
    private readonly Action<string> _sink;
    private readonly TextWriter? _tee;

    private readonly StringBuilder _line = new();

    public UiTextWriter(Action<string> sink, TextWriter? tee = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _tee = tee;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        // Keep original output too (Debug Output / console), if provided
        _tee?.Write(value);

        if (value == '\r') return;

        if (value == '\n')
        {
            FlushLine();
            return;
        }

        _line.Append(value);
    }

    public override void Write(string? value)
    {
        if (value is null) return;

        _tee?.Write(value);

        foreach (var ch in value)
            Write(ch);
    }

    public override void WriteLine(string? value)
    {
        _tee?.WriteLine(value);

        if (value is not null)
            _line.Append(value);

        FlushLine();
    }

    public override void Flush()
    {
        _tee?.Flush();
        FlushLine();
    }

    private void FlushLine()
    {
        if (_line.Length == 0) return;

        var s = _line.ToString();
        _line.Clear();

        // Send one complete line to UI
        _sink(s);
    }
}
