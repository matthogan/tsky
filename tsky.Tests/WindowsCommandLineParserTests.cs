using System.Runtime.InteropServices;

namespace tsky.Tests;

public sealed class WindowsCommandLineParserTests
{
    [Theory]
    [InlineData("tool.exe arg1 arg2", new[] { "tool.exe", "arg1", "arg2" })]
    [InlineData("\"C:\\Program Files\\tool.exe\" /quiet", new[] { "C:\\Program Files\\tool.exe", "/quiet" })]
    [InlineData("python \"script name.py\" --mode fast", new[] { "python", "script name.py", "--mode", "fast" })]
    [InlineData("app.exe \"\"", new[] { "app.exe", "" })]
    public void Tokenize_BasicQuotingAndWhitespace_ParsesExpectedTokens(string input, string[] expected)
    {
        var tokens = WindowsCommandLineParser.Tokenize(input);

        Assert.Equal(expected, tokens);
    }

    [Fact]
    public void Tokenize_DoubledQuotesInsideQuotedSegment_ParsesLiteralQuote()
    {
        var input = "app.exe \"a\"\"b\"";

        var tokens = WindowsCommandLineParser.Tokenize(input);

        Assert.Equal(["app.exe", "a\"b"], tokens);
    }

    [Fact]
    public void Tokenize_OddBackslashesBeforeQuote_EscapesQuote()
    {
        var input = "app.exe \"" + "C:" + new string('\\', 3) + "\"path\"";

        var tokens = WindowsCommandLineParser.Tokenize(input);

        Assert.Equal(["app.exe", "C:\\\"path"], tokens);
    }

    [Fact]
    public void Tokenize_EvenBackslashesBeforeQuote_TogglesQuoteAndPreservesHalfBackslashes()
    {
        var input = "app.exe \"" + "C:" + new string('\\', 2) + "\" tail";

        var tokens = WindowsCommandLineParser.Tokenize(input);

        Assert.Equal(["app.exe", "C:\\", "tail"], tokens);
    }

    [Fact]
    public void Tokenize_UnmatchedQuote_ConsumesRemainderOfArgument()
    {
        var input = "app.exe \"hello world";

        var tokens = WindowsCommandLineParser.Tokenize(input);

        Assert.Equal(["app.exe", "hello world"], tokens);
    }

    [Fact]
    public void Tokenize_MatchesCommandLineToArgvW_ForTrickyInputs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var trickyInputs = new[]
        {
            "tool.exe arg1 arg2",
            "\"C:\\Program Files\\tool.exe\" /quiet",
            "python \"script name.py\" --mode fast",
            "app.exe \"\"",
            "app.exe \"a\"\"b\"",
            "app.exe \"C:" + new string('\\', 3) + "\"path\"",
            "app.exe \"C:" + new string('\\', 2) + "\" tail",
            "app.exe \"hello world",
            "  app.exe   one    two   ",
            "app.exe \"trailing\\\\\"quote\""
        };

        foreach (var input in trickyInputs)
        {
            var expected = ParseWithCommandLineToArgvW(input);
            var actual = WindowsCommandLineParser.Tokenize(input);
            Assert.Equal(expected, actual);
        }
    }

    private static List<string> ParseWithCommandLineToArgvW(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        if (argv == IntPtr.Zero)
        {
            throw new InvalidOperationException("CommandLineToArgvW returned a null pointer.");
        }

        try
        {
            var tokens = new List<string>(argc);
            for (var i = 0; i < argc; i++)
            {
                var pointer = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                var value = Marshal.PtrToStringUni(pointer);
                tokens.Add(value ?? string.Empty);
            }

            return tokens;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
