using System.Text;

namespace FpsUtility.Core.Raw;

/// <summary>
/// Reads the library folder paths out of Steam's <c>libraryfolders.vdf</c>, which the collector needs
/// to know which folders to read. It understands only what that needs: quoted and bare tokens,
/// braces, <c>//</c> comments and backslash escapes. Both layouts are read: the current one
/// (<c>"0" { "path" "…" … }</c>) and the older one (<c>"1" "D:\\SteamLibrary"</c>). Everything else
/// in the file is left to the normaliser, which reads the file text kept in the snapshot.
/// </summary>
public static class SteamLibraryFolders
{
    /// <exception cref="FormatException">The text isn't a <c>libraryfolders</c> VDF document.</exception>
    public static IReadOnlyList<string> ParsePaths(string vdf)
    {
        ArgumentNullException.ThrowIfNull(vdf);

        var tokens = Tokenize(vdf);
        var position = 0;

        var root = Next(tokens, ref position);
        if (root.Kind != TokenKind.Text || !root.Text.Equals("libraryfolders", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("The document's root key is not \"libraryfolders\".");
        }

        Expect(tokens, ref position, TokenKind.Open);
        var paths = new List<string>();

        while (true)
        {
            var key = Next(tokens, ref position);
            if (key.Kind == TokenKind.Close)
            {
                return paths;
            }

            if (key.Kind != TokenKind.Text)
            {
                throw new FormatException("Expected a library key.");
            }

            var value = Next(tokens, ref position);
            var isLibrary = key.Text.Length > 0 && key.Text.All(char.IsAsciiDigit);

            if (value.Kind == TokenKind.Text)
            {
                if (isLibrary)
                {
                    paths.Add(value.Text); // older layout: the value is the path
                }
            }
            else if (value.Kind == TokenKind.Open)
            {
                var path = ReadEntryPath(tokens, ref position);
                if (isLibrary && path is not null)
                {
                    paths.Add(path);
                }
            }
            else
            {
                throw new FormatException("Expected a value or a block after a key.");
            }
        }
    }

    /// <summary>Reads one library block up to its closing brace; returns its <c>path</c> value, if any.</summary>
    private static string? ReadEntryPath(List<Token> tokens, ref int position)
    {
        string? path = null;

        while (true)
        {
            var key = Next(tokens, ref position);
            if (key.Kind == TokenKind.Close)
            {
                return path;
            }

            if (key.Kind != TokenKind.Text)
            {
                throw new FormatException("Expected a key inside a library block.");
            }

            var value = Next(tokens, ref position);
            if (value.Kind == TokenKind.Open)
            {
                SkipBlock(tokens, ref position);
            }
            else if (value.Kind == TokenKind.Text)
            {
                if (key.Text.Equals("path", StringComparison.OrdinalIgnoreCase))
                {
                    path = value.Text;
                }
            }
            else
            {
                throw new FormatException("Expected a value or a block after a key.");
            }
        }
    }

    private static void SkipBlock(List<Token> tokens, ref int position)
    {
        var depth = 1;
        while (depth > 0)
        {
            var token = Next(tokens, ref position);
            depth += token.Kind switch
            {
                TokenKind.Open => 1,
                TokenKind.Close => -1,
                _ => 0,
            };
        }
    }

    private static Token Next(List<Token> tokens, ref int position) =>
        position < tokens.Count ? tokens[position++] : throw new FormatException("The document ends inside a block.");

    private static void Expect(List<Token> tokens, ref int position, TokenKind kind)
    {
        if (Next(tokens, ref position).Kind != kind)
        {
            throw new FormatException($"Expected {kind}.");
        }
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '{')
            {
                tokens.Add(new Token(TokenKind.Open, "{"));
                i++;
            }
            else if (c == '}')
            {
                tokens.Add(new Token(TokenKind.Close, "}"));
                i++;
            }
            else if (c == '"')
            {
                tokens.Add(new Token(TokenKind.Text, ReadQuoted(text, ref i)));
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Text, text[start..i]));
            }
        }

        return tokens;
    }

    private static string ReadQuoted(string text, ref int i)
    {
        var value = new StringBuilder();
        i++; // opening quote

        while (i < text.Length)
        {
            var c = text[i++];
            if (c == '"')
            {
                return value.ToString();
            }

            if (c == '\\' && i < text.Length)
            {
                var escaped = text[i++];
                value.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    _ => escaped, // \\ and \" and anything else: the character itself
                });
            }
            else
            {
                value.Append(c);
            }
        }

        throw new FormatException("A quoted string is not closed.");
    }

    private enum TokenKind
    {
        Text,
        Open,
        Close,
    }

    private readonly record struct Token(TokenKind Kind, string Text);
}
