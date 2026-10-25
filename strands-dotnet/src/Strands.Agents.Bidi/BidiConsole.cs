using System.Runtime.CompilerServices;
using System.Text;

namespace Strands.Agents.Bidi;

/// <summary>Native text input and readable output for bidirectional conversations.</summary>
public static partial class BidiConsole
{
    private static int _keyboardInUse;

    /// <summary>Reads an exclusive interactive keyboard; redirected input requires the line-callback overload.</summary>
    /// <param name="echo">Borrowed echo writer; null selects Console.Out and TextWriter.Null disables echo.</param>
    /// <param name="cancellationToken">Cooperatively stops keyboard polling.</param>
    /// <remarks>Do not read the keyboard elsewhere while enumerating. Synchronous console calls cannot be interrupted once entered.</remarks>
    public static IAsyncEnumerable<IBidiInput> Input(TextWriter? echo = null, CancellationToken cancellationToken = default)
        => InputFromKeyboard(() => Console.IsInputRedirected, () => Console.KeyAvailable,
            () => Console.ReadKey(intercept: true), echo ?? Console.Out, cancellationToken);

    /// <summary>Reads lines through a borrowed capability that must cooperate with cancellation.</summary>
    /// <param name="readLine">Reads one line; null means normal EOF. No pending read is abandoned.</param>
    /// <param name="cancellationToken">Cooperatively stops the supplied reader.</param>
    /// <remarks>An uncancellable reader delays shutdown until its pending read returns. The SDK does not close the reader.</remarks>
    public static IAsyncEnumerable<IBidiInput> Input(Func<CancellationToken, ValueTask<string?>> readLine,
        CancellationToken cancellationToken = default)
    {
        if (readLine is null) throw new ArgumentNullException(nameof(readLine));
        return ReadLinesAsync(readLine, cancellationToken);
    }

    /// <summary>Creates awaited role-labelled output without adopting the writer.</summary>
    /// <param name="writer">Borrowed output writer; null selects Console.Out.</param>
    /// <param name="options">Optional output-category settings.</param>
    /// <remarks>Writes and flushes are awaited without closing the writer. Only recent final-event duplicates are suppressed.</remarks>
    public static IBidiOutputStream Output(TextWriter? writer = null, BidiConsoleOutputOptions? options = null)
        => new ConsoleOutput(writer ?? Console.Out, options ?? new());

    internal static async IAsyncEnumerable<IBidiInput> InputFromKeyboard(Func<bool> inputRedirected,
        Func<bool> keyAvailable, Func<ConsoleKeyInfo> readKey, TextWriter echo,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (inputRedirected()) throw new NotSupportedException("Interactive keyboard input requires a console. Use the cancellation-aware line callback for redirected input.");
        if (Interlocked.CompareExchange(ref _keyboardInUse, 1, 0) != 0)
            throw new InvalidOperationException("The interactive keyboard already has an active reader.");
        try
        {
            var line = new StringBuilder();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!keyAvailable())
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var key = readKey();
                if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key is ConsoleKey.D or ConsoleKey.Z)
                {
                    if (line.Length > 0) yield return new BidiMessage([new TextBlock(line.ToString())]);
                    yield break;
                }
                if (key.Key == ConsoleKey.Enter)
                {
                    await echo.WriteLineAsync().ConfigureAwait(false);
                    await echo.FlushAsync().ConfigureAwait(false);
                    if (line.Length > 0) yield return new BidiMessage([new TextBlock(line.ToString())]);
                    line.Clear();
                }
                else if (key.Key == ConsoleKey.Backspace && line.Length > 0)
                {
                    var count = line.Length > 1 && char.IsLowSurrogate(line[line.Length - 1]) && char.IsHighSurrogate(line[line.Length - 2]) ? 2 : 1;
                    line.Length -= count;
                    await echo.WriteAsync("\b \b").ConfigureAwait(false);
                }
                else if (!char.IsControl(key.KeyChar) || key.KeyChar == '\t')
                {
                    line.Append(key.KeyChar);
                    await echo.WriteAsync(key.KeyChar).ConfigureAwait(false);
                }
            }
        }
        finally { Interlocked.Exchange(ref _keyboardInUse, 0); }
    }

    private static async IAsyncEnumerable<IBidiInput> ReadLinesAsync(Func<CancellationToken, ValueTask<string?>> readLine,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await readLine(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (line is null) yield break;
            if (line.Length > 0) yield return new BidiMessage([new TextBlock(line)]);
        }
    }
}
