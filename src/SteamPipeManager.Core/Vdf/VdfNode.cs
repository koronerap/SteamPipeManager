using System.Diagnostics;

namespace SteamPipeManager.Core.Vdf;

/// <summary>
/// Valve KeyValues ağacındaki tek bir düğüm. Ya bir değer taşır (leaf) ya da alt düğümler (block).
/// Anahtar karşılaştırmaları büyük/küçük harf duyarsızdır: referans script'lerde hem
/// <c>"AppID"</c> hem <c>"appid"</c> geçiyor.
/// </summary>
[DebuggerDisplay("{Key,nq} = {Value,nq} ({Children.Count} children)")]
public sealed class VdfNode
{
    private VdfNode(string key, string? value, List<VdfNode>? children)
    {
        Key = key;
        Value = value;
        Children = children ?? [];
        IsBlock = children is not null;
    }

    public string Key { get; }

    /// <summary>Leaf düğümün değeri; block düğümlerde null.</summary>
    public string? Value { get; }

    public List<VdfNode> Children { get; }

    public bool IsBlock { get; }

    public static VdfNode Leaf(string key, string value) => new(key, value, null);

    public static VdfNode Block(string key, IEnumerable<VdfNode>? children = null) =>
        new(key, null, children is null ? [] : [.. children]);

    public VdfNode Add(VdfNode child)
    {
        if (!IsBlock)
        {
            throw new InvalidOperationException($"'{Key}' is a leaf node; children cannot be added.");
        }

        Children.Add(child);
        return this;
    }

    public VdfNode Add(string key, string value) => Add(Leaf(key, value));

    public VdfNode? Child(string key) =>
        Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<VdfNode> ChildrenNamed(string key) =>
        Children.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Leaf değerini döndürür; anahtar yoksa veya block ise null.</summary>
    public string? ValueOf(string key) => Child(key) is { IsBlock: false } leaf ? leaf.Value : null;

    public string ValueOf(string key, string fallback) => ValueOf(key) ?? fallback;

    /// <summary>VDF'de boolean'lar <c>"0"</c>/<c>"1"</c> olarak yazılır.</summary>
    public bool BoolOf(string key, bool fallback = false) =>
        ValueOf(key) is { } raw ? raw.Trim() == "1" : fallback;

    public uint? UIntOf(string key) =>
        uint.TryParse(ValueOf(key)?.Trim(), out var parsed) ? parsed : null;
}
