using System.Text;

namespace Mesura.Core.Config;

/// <summary>
/// A mutable Valve KeyValues ("VDF") document that round-trips: it is parsed, edited at named paths, and written back
/// with every unknown key preserved in its original order (the import rule: "modify only explicitly supported keys").
/// It is written in Steam's own canonical form (tab indent, quoted key/value, braces on their own lines), which is how
/// Steam re-serialises these files itself, so re-importing the same settings is idempotent.
/// </summary>
/// <remarks>
/// This is a deliberately small, pure parser for the one job of surgical edits. The read-only
/// <see cref="Raw.Vdf"/> stays the reader for the normaliser; this one is the editor for the config port.
/// Nesting deeper than <see cref="MaxDepth"/> is refused, as in <see cref="Raw.Vdf"/>, so a damaged file can't overflow
/// the stack. Keys are matched case-insensitively, as Steam reads them.
/// </remarks>
public sealed class VdfDocument
{
    public const int MaxDepth = 64;

    private readonly List<VdfEntry> _roots;

    private VdfDocument(List<VdfEntry> roots) => _roots = roots;

    /// <exception cref="FormatException">The text is not a well-formed KeyValues document.</exception>
    public static VdfDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var position = 0;
        var roots = new List<VdfEntry>();
        while (true)
        {
            SkipTrivia(text, ref position);
            if (position >= text.Length)
            {
                break;
            }

            roots.Add(ReadEntry(text, ref position, depth: 1));
        }

        return new VdfDocument(roots);
    }

    /// <summary>The single root block Steam's files have, by name (case-insensitive), or <c>null</c>.</summary>
    public VdfEntry? Root(string key) => _roots.Find(e => e.IsBlock && e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first root block, whatever it is called (Steam's config files have exactly one).</summary>
    public VdfEntry? FirstRoot => _roots.Find(e => e.IsBlock);

    /// <summary>
    /// The leaf value at a <c>/</c>-separated path from a root, or <c>null</c> if any segment is absent or the final
    /// segment is a block rather than a value.
    /// </summary>
    public string? GetValue(string path)
    {
        var (parent, leaf) = Resolve(SplitPath(path), create: false);
        return parent?.Child(leaf) is { IsBlock: false } node ? node.Value : null;
    }

    /// <summary>
    /// Sets the leaf value at a <c>/</c>-separated path, creating intermediate blocks as needed. An existing leaf is
    /// replaced in place (so importing twice changes nothing and adds nothing); an existing block at that name is left
    /// untouched and <c>false</c> is returned, because overwriting a block with a value would discard unknown keys.
    /// </summary>
    public bool SetValue(string path, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var segments = SplitPath(path);
        var (parent, leaf) = Resolve(segments, create: true);
        if (parent is null)
        {
            return false;
        }

        var existing = parent.Child(leaf);
        if (existing is { IsBlock: true })
        {
            return false;
        }

        if (existing is not null)
        {
            existing.Value = value;
        }
        else
        {
            parent.AddChild(VdfEntry.Leaf(leaf, value));
        }

        return true;
    }

    public string ToText()
    {
        var builder = new StringBuilder();
        foreach (var entry in _roots)
        {
            entry.Write(builder, 0);
        }

        return builder.ToString();
    }

    private (VdfEntry? Parent, string Leaf) Resolve(IReadOnlyList<string> segments, bool create)
    {
        if (segments.Count < 2)
        {
            throw new ArgumentException("A config path needs a root block and at least one key.", nameof(segments));
        }

        var current = create ? EnsureRoot(segments[0]) : Root(segments[0]);
        for (var i = 1; i < segments.Count - 1 && current is not null; i++)
        {
            current = create ? current.EnsureBlock(segments[i]) : current.Child(segments[i]) is { IsBlock: true } b ? b : null;
        }

        return (current, segments[^1]);
    }

    private VdfEntry EnsureRoot(string key)
    {
        var root = Root(key);
        if (root is null)
        {
            root = VdfEntry.Block(key);
            _roots.Add(root);
        }

        return root;
    }

    private static string[] SplitPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static VdfEntry ReadEntry(string text, ref int position, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException($"KeyValues nesting deeper than {MaxDepth} levels.");
        }

        var key = ReadToken(text, ref position);
        SkipTrivia(text, ref position);
        if (position >= text.Length)
        {
            throw new FormatException($"Key \"{key}\" has no value or block.");
        }

        if (text[position] == '{')
        {
            position++;
            var children = new List<VdfEntry>();
            while (true)
            {
                SkipTrivia(text, ref position);
                if (position >= text.Length)
                {
                    throw new FormatException($"Block \"{key}\" is not closed.");
                }

                if (text[position] == '}')
                {
                    position++;
                    break;
                }

                children.Add(ReadEntry(text, ref position, depth + 1));
            }

            return VdfEntry.Block(key, children);
        }

        return VdfEntry.Leaf(key, ReadToken(text, ref position));
    }

    private static string ReadToken(string text, ref int position)
    {
        SkipTrivia(text, ref position);
        if (position >= text.Length)
        {
            throw new FormatException("Expected a token but reached the end of the document.");
        }

        if (text[position] == '{' || text[position] == '}')
        {
            throw new FormatException($"Expected a key or value, found '{text[position]}'.");
        }

        return text[position] == '"' ? ReadQuoted(text, ref position) : ReadBare(text, ref position);
    }

    private static string ReadQuoted(string text, ref int position)
    {
        position++; // opening quote
        var builder = new StringBuilder();
        while (position < text.Length)
        {
            var c = text[position++];
            if (c == '"')
            {
                return builder.ToString();
            }

            if (c == '\\' && position < text.Length)
            {
                var next = text[position++];
                builder.Append(next switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    _ => next, // \\ and \" and anything else: the escaped char literally
                });
            }
            else
            {
                builder.Append(c);
            }
        }

        throw new FormatException("A quoted token is not closed.");
    }

    private static string ReadBare(string text, ref int position)
    {
        var start = position;
        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"'))
        {
            position++;
        }

        return text[start..position];
    }

    private static void SkipTrivia(string text, ref int position)
    {
        while (position < text.Length)
        {
            var c = text[position];
            if (char.IsWhiteSpace(c))
            {
                position++;
            }
            else if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] is not ('\n' or '\r'))
                {
                    position++;
                }
            }
            else
            {
                return;
            }
        }
    }
}

/// <summary>One VDF key: a text value (<see cref="IsBlock"/> false) or a block of ordered child keys.</summary>
public sealed class VdfEntry
{
    private readonly List<VdfEntry>? _children;

    private VdfEntry(string key, string? value, List<VdfEntry>? children)
    {
        Key = key;
        Value = value;
        _children = children;
    }

    public string Key { get; }

    /// <summary>The value of a leaf; the mutable target of <see cref="VdfDocument.SetValue"/>. <c>null</c> for a block.</summary>
    public string? Value { get; internal set; }

    public bool IsBlock => _children is not null;

    public IReadOnlyList<VdfEntry> Children => _children ?? [];

    internal static VdfEntry Leaf(string key, string value) => new(key, value, null);

    internal static VdfEntry Block(string key, List<VdfEntry>? children = null) => new(key, null, children ?? []);

    public VdfEntry? Child(string key) => _children?.Find(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    internal void AddChild(VdfEntry entry) => _children!.Add(entry);

    internal VdfEntry EnsureBlock(string key)
    {
        var existing = Child(key);
        if (existing is { IsBlock: true })
        {
            return existing;
        }

        if (existing is not null)
        {
            throw new InvalidOperationException($"\"{key}\" is a value, not a block, so a child cannot be set under it.");
        }

        var block = Block(key);
        _children!.Add(block);
        return block;
    }

    internal void Write(StringBuilder builder, int depth)
    {
        var indent = new string('\t', depth);
        if (IsBlock)
        {
            builder.Append(indent).Append('"').Append(Escape(Key)).Append('"').Append('\n');
            builder.Append(indent).Append('{').Append('\n');
            foreach (var child in _children!)
            {
                child.Write(builder, depth + 1);
            }

            builder.Append(indent).Append('}').Append('\n');
        }
        else
        {
            builder.Append(indent).Append('"').Append(Escape(Key)).Append("\"\t\t\"").Append(Escape(Value!)).Append('"').Append('\n');
        }
    }

    // Only the backslash and the quote are escaped. Newlines and tabs inside a value are written as they are, which is
    // how Steam writes them (its controller-mapping strings in config.vdf hold raw newlines), so a file round-trips.
    private static string Escape(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal)
         .Replace("\"", "\\\"", StringComparison.Ordinal);
}
