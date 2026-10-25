using System.Text;

namespace Mesura.Core.Raw;

/// <summary>
/// One key of a Valve KeyValues ("VDF") document: either a text value or a block of child keys, in file order.
/// Keys may repeat, as the format allows.
/// </summary>
internal sealed class VdfNode
{
    public VdfNode(string key, string text)
    {
        Key = key;
        Text = text;
        Children = [];
    }

    public VdfNode(string key, IReadOnlyList<VdfNode> children)
    {
        Key = key;
        Children = children;
    }

    public string Key { get; }

    /// <summary>The value when the key holds text; <c>null</c> when it holds a block.</summary>
    public string? Text { get; }

    public IReadOnlyList<VdfNode> Children { get; }

    public bool IsBlock => Text is null;

    /// <summary>The first child with this key (case-insensitive, as Steam reads keys), or <c>null</c>.</summary>
    public VdfNode? Child(string key) => Children.FirstOrDefault(child => child.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads the Valve KeyValues text Steam writes (<c>libraryfolders.vdf</c>, <c>appmanifest_*.acf</c>): quoted and
/// bare tokens, braces, <c>//</c> comments and backslash escapes. Only the first top-level key is read; Steam's files
/// have one. Conditional tags (<c>[$WIN32]</c>) and <c>#include</c> are not supported; Steam's library files don't use them.
/// Nesting deeper than <see cref="MaxDepth"/> is refused as malformed: Steam's files nest about four levels, and an
/// unbounded depth would let one damaged file overflow the stack and end the whole process.
/// </summary>
internal static class Vdf
{
    public const int MaxDepth = 64;

    /// <exception cref="FormatException">The text isn't a well-formed KeyValues document.</exception>
    public static VdfNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = Tokenize(text);
        var position = 0;
        return ReadPair(tokens, ref position, depth: 1);
    }

    private static VdfNode ReadPair(List<Token> tokens, ref int position, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException($"The document nests deeper than {MaxDepth} levels.");
        }

        var key = Next(tokens, ref position);
        if (key.Kind != TokenKind.Text)
        {
            throw new FormatException("Expected a key.");
        }

        var value = Next(tokens, ref position);
        return value.Kind switch
        {
            TokenKind.Text => new VdfNode(key.Text, value.Text),
            TokenKind.Open => new VdfNode(key.Text, ReadBlock(tokens, ref position, depth)),
            _ => throw new FormatException("Expected a value or a block after a key."),
        };
    }

    private static List<VdfNode> ReadBlock(List<Token> tokens, ref int position, int depth)
    {
        var children = new List<VdfNode>();
        while (true)
        {
            if (Peek(tokens, position).Kind == TokenKind.Close)
            {
                position++;
                return children;
            }

            children.Add(ReadPair(tokens, ref position, depth + 1));
        }
    }

    private static Token Peek(List<Token> tokens, int position) =>
        position < tokens.Count ? tokens[position] : throw new FormatException("The document ends inside a block.");

    private static Token Next(List<Token> tokens, ref int position) =>
        position < tokens.Count ? tokens[position++] : throw new FormatException("The document ends inside a block.");

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
