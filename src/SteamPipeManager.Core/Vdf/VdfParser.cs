namespace SteamPipeManager.Core.Vdf;

public sealed class VdfParseException(string message, int line) : Exception($"{message} (line {line})")
{
    public int Line { get; } = line;
}

/// <summary>
/// ContentBuilder script'lerini okuyan KeyValues ayrıştırıcısı.
///
/// Kasıtlı olarak <b>ters bölü kaçışları işlemez</b>: build script'leri Windows yollarını
/// kaçışsız yazar (<c>"D:\sdk\tools\output"</c>). Kaçışlar işlenseydi <c>\t</c> sekmeye,
/// <c>\s</c> geçersiz kaçışa dönüşür ve yollar bozulurdu.
/// </summary>
public static class VdfParser
{
    public static VdfNode ParseFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>Kökte birden fazla düğüm olabileceği için sonuç isimsiz bir block içinde döner.</summary>
    public static VdfNode Parse(string text)
    {
        var reader = new TokenReader(text);
        var root = VdfNode.Block("");
        ParseInto(root, reader, depth: 0);

        if (reader.TryPeek(out var leftover))
        {
            throw new VdfParseException($"Unexpected '{leftover.Text}'", leftover.Line);
        }

        return root;
    }

    /// <summary>Tek kök düğümü olan script'ler için kısayol (app ve depot script'leri böyledir).</summary>
    public static VdfNode ParseSingleRoot(string text)
    {
        var root = Parse(text);
        return root.Children.Count == 1
            ? root.Children[0]
            : throw new VdfParseException($"Expected a single root node, found {root.Children.Count}", 0);
    }

    public static VdfNode ParseSingleRootFile(string path) => ParseSingleRoot(File.ReadAllText(path));

    private static void ParseInto(VdfNode parent, TokenReader reader, int depth)
    {
        while (reader.TryPeek(out var token))
        {
            if (token.Kind == TokenKind.CloseBrace)
            {
                if (depth == 0)
                {
                    throw new VdfParseException("Unmatched '}'", token.Line);
                }

                return;
            }

            if (token.Kind == TokenKind.OpenBrace)
            {
                throw new VdfParseException("'{' without a key", token.Line);
            }

            reader.Read();
            var key = token.Text;

            if (!reader.TryPeek(out var next))
            {
                throw new VdfParseException($"'{key}' has no value", token.Line);
            }

            if (next.Kind == TokenKind.OpenBrace)
            {
                reader.Read();
                var block = VdfNode.Block(key);
                ParseInto(block, reader, depth + 1);

                if (!reader.TryRead(out var closing) || closing.Kind != TokenKind.CloseBrace)
                {
                    throw new VdfParseException($"'{key}' block is not closed", token.Line);
                }

                parent.Add(block);
                continue;
            }

            if (next.Kind == TokenKind.CloseBrace)
            {
                throw new VdfParseException($"'{key}' has no value", token.Line);
            }

            reader.Read();
            parent.Add(VdfNode.Leaf(key, next.Text));
        }
    }

    private enum TokenKind
    {
        Text,
        OpenBrace,
        CloseBrace,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Line);

    /// <summary>Tembel tokenizer: tek token ileri bakış yeter.</summary>
    private sealed class TokenReader(string text)
    {
        private readonly string _text = text;
        private int _pos;
        private int _line = 1;
        private Token? _peeked;

        public bool TryPeek(out Token token)
        {
            if (_peeked is null && Next() is { } scanned)
            {
                _peeked = scanned;
            }

            token = _peeked ?? default;
            return _peeked is not null;
        }

        public bool TryRead(out Token token)
        {
            var ok = TryPeek(out token);
            _peeked = null;
            return ok;
        }

        public void Read() => _peeked = null;

        private Token? Next()
        {
            SkipTrivia();

            if (_pos >= _text.Length)
            {
                return null;
            }

            var c = _text[_pos];
            var line = _line;

            switch (c)
            {
                case '{':
                    _pos++;
                    return new Token(TokenKind.OpenBrace, "{", line);
                case '}':
                    _pos++;
                    return new Token(TokenKind.CloseBrace, "}", line);
                case '"':
                    return new Token(TokenKind.Text, ReadQuoted(), line);
                default:
                    return new Token(TokenKind.Text, ReadBare(), line);
            }
        }

        private string ReadQuoted()
        {
            var startLine = _line;
            _pos++; // açılış tırnağı
            var start = _pos;

            while (_pos < _text.Length && _text[_pos] != '"')
            {
                if (_text[_pos] == '\n')
                {
                    throw new VdfParseException("Unterminated quote", startLine);
                }

                _pos++;
            }

            if (_pos >= _text.Length)
            {
                throw new VdfParseException("Unterminated quote", startLine);
            }

            var value = _text[start.._pos];
            _pos++; // kapanış tırnağı
            return value;
        }

        private string ReadBare()
        {
            var start = _pos;

            while (_pos < _text.Length && !IsBareTerminator(_text[_pos]))
            {
                _pos++;
            }

            return _text[start.._pos];
        }

        private static bool IsBareTerminator(char c) =>
            char.IsWhiteSpace(c) || c is '{' or '}' or '"';

        private void SkipTrivia()
        {
            while (_pos < _text.Length)
            {
                var c = _text[_pos];

                if (c == '\n')
                {
                    _line++;
                    _pos++;
                }
                else if (char.IsWhiteSpace(c))
                {
                    _pos++;
                }
                else if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
                {
                    while (_pos < _text.Length && _text[_pos] != '\n')
                    {
                        _pos++;
                    }
                }
                else if (c == '[')
                {
                    // KeyValues koşul eki (ör. [$WIN32]); build script'lerinde anlamı yok, atlanır.
                    while (_pos < _text.Length && _text[_pos] != ']' && _text[_pos] != '\n')
                    {
                        _pos++;
                    }

                    if (_pos < _text.Length && _text[_pos] == ']')
                    {
                        _pos++;
                    }
                }
                else
                {
                    return;
                }
            }
        }
    }
}
