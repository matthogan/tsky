using System.Text;

namespace tsky;

internal static class WindowsCommandLineParser
{
    internal static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return tokens;
        }

        var span = commandLine.AsSpan();
        var index = 0;

        // CommandLineToArgvW behavior: if the command line starts with
        // whitespace, argv[0] is an empty string.
        if (char.IsWhiteSpace(span[0]))
        {
            tokens.Add(string.Empty);
        }

        while (index < span.Length)
        {
            while (index < span.Length && char.IsWhiteSpace(span[index]))
            {
                index++;
            }

            if (index >= span.Length)
            {
                break;
            }

            var current = new StringBuilder();
            var inQuotes = false;

            while (index < span.Length)
            {
                var backslashCount = 0;
                while (index < span.Length && span[index] == '\\')
                {
                    backslashCount++;
                    index++;
                }

                if (index < span.Length && span[index] == '"')
                {
                    // CommandLineToArgvW-style handling:
                    // Even number of backslashes before a quote toggles quote mode.
                    // Odd number escapes the quote.
                    current.Append('\\', backslashCount / 2);

                    if (backslashCount % 2 == 0)
                    {
                        if (inQuotes && index + 1 < span.Length && span[index + 1] == '"')
                        {
                            current.Append('"');
                            index += 2;
                        }
                        else
                        {
                            inQuotes = !inQuotes;
                            index++;
                        }
                    }
                    else
                    {
                        current.Append('"');
                        index++;
                    }

                    continue;
                }

                if (backslashCount > 0)
                {
                    current.Append('\\', backslashCount);
                }

                if (index >= span.Length)
                {
                    break;
                }

                if (!inQuotes && char.IsWhiteSpace(span[index]))
                {
                    break;
                }

                current.Append(span[index]);
                index++;
            }

            tokens.Add(current.ToString());

            while (index < span.Length && char.IsWhiteSpace(span[index]))
            {
                index++;
            }
        }

        return tokens;
    }
}
