using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace OhMyPc.Infrastructure.CliProxy;

/// <summary>YamlDotNet 表示模型的辅助方法：以结构化编辑方式读写 YAML，只改动显式赋值的键，其余节点原样保留。</summary>
internal static class YamlTree
{
    /// <summary>会被 YAML 解析为数字的字符串（如 "123456"、"0.5"），作为文本写出时必须加引号。</summary>
    private static readonly Regex NumberLike = new("^[-+]?(\\d+(\\.\\d*)?|\\.\\d+)([eE][-+]?\\d+)?$", RegexOptions.Compiled);

    /// <summary>这些词作为纯量会被弱类型解析器读成布尔/空值，作为文本写出时必须加引号。</summary>
    private static readonly HashSet<string> ReservedWords =
        ["true", "false", "null", "yes", "no", "on", "off", "~"];

    public static YamlScalarNode Key(string name) => new(name);

    /// <summary>显式纯量：调用方保证这是数值/布尔，必须不带引号。</summary>
    public static YamlScalarNode Plain(string value) => new(value) { Style = ScalarStyle.Plain };

    /// <summary>字符串文本：仅当纯量形式会被误读为数字/布尔/空值或包含歧义字符时加引号。</summary>
    public static YamlScalarNode Text(string value) =>
        new(value) { Style = IsSafePlain(value) ? ScalarStyle.Plain : ScalarStyle.SingleQuoted };

    private static bool IsSafePlain(string value) =>
        value.Length > 0
        && !ReservedWords.Contains(value)
        && !NumberLike.IsMatch(value)
        && !value.Contains(": ")
        && !value.Contains(" #")
        && value[0] is not (' ' or '\t' or '\'' or '"')
        && value[^1] is not (' ' or '\t');

    public static string? Scalar(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(Key(key), out var node) ? (node as YamlScalarNode)?.Value : null;

    public static bool TryGetBoolean(YamlMappingNode map, string key, bool fallback) =>
        bool.TryParse(Scalar(map, key), out var value) ? value : fallback;

    public static int GetInt32(YamlMappingNode map, string key, int fallback) =>
        int.TryParse(Scalar(map, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public static long? GetInt64OrNull(YamlMappingNode map, string key) =>
        long.TryParse(Scalar(map, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static int? GetInt32OrNull(YamlMappingNode map, string key) =>
        int.TryParse(Scalar(map, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static YamlMappingNode GetOrCreateMapping(YamlMappingNode parent, string key)
    {
        if (parent.Children.TryGetValue(Key(key), out var node) && node is YamlMappingNode mapping) return mapping;
        mapping = new YamlMappingNode();
        parent.Children[Key(key)] = mapping;
        return mapping;
    }

    public static YamlSequenceNode? Sequence(YamlMappingNode parent, string key) =>
        parent.Children.TryGetValue(Key(key), out var node) ? node as YamlSequenceNode : null;

    public static YamlMappingNode? Mapping(YamlMappingNode parent, string key) =>
        parent.Children.TryGetValue(Key(key), out var node) ? node as YamlMappingNode : null;

    public static decimal? GetDecimalOrNull(YamlMappingNode map, string key) =>
        decimal.TryParse(Scalar(map, key), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static IReadOnlyList<string> StringList(YamlMappingNode parent, string key) =>
        Sequence(parent, key)?.Children.OfType<YamlScalarNode>().Select(node => node.Value ?? "").ToList() ?? [];

    public static void SetScalar(YamlMappingNode parent, string key, string value) =>
        parent.Children[Key(key)] = Text(value);

    public static void SetScalar(YamlMappingNode parent, string key, int value) =>
        parent.Children[Key(key)] = Plain(value.ToString(CultureInfo.InvariantCulture));

    public static void SetStringList(YamlMappingNode parent, string key, IEnumerable<string> values) =>
        parent.Children[Key(key)] = new YamlSequenceNode(values.Select(v => (YamlNode)Text(v)));

    public static void Remove(YamlMappingNode parent, string key) => parent.Children.Remove(Key(key));

    /// <summary>把纯字典树（字符串/数值/布尔/列表/嵌套字典）转换为 YAML 节点。</summary>
    public static YamlNode ToNode(object? value) => value switch
    {
        null => Text(""),
        bool b => Plain(b ? "true" : "false"),
        int i => Plain(i.ToString(CultureInfo.InvariantCulture)),
        long l => Plain(l.ToString(CultureInfo.InvariantCulture)),
        decimal m => Plain(m.ToString(CultureInfo.InvariantCulture)),
        string s => Text(s),
        IEnumerable<string> list => new YamlSequenceNode(list.Select(v => (YamlNode)Text(v))),
        Dictionary<string, object?> map => new YamlMappingNode(map.Select(pair =>
            new KeyValuePair<YamlNode, YamlNode>(Key(pair.Key), ToNode(pair.Value)))),
        _ => throw new NotSupportedException($"不支持的 YAML 值类型：{value.GetType().Name}")
    };

    /// <summary>把字典树合并进已有 YAML 映射：仅覆盖给出的键，保留其余键。</summary>
    public static void MergeInto(YamlMappingNode target, Dictionary<string, object?> values)
    {
        foreach (var (key, value) in values) target.Children[Key(key)] = ToNode(value);
    }

    public static async Task<YamlMappingNode> ReadRootAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new YamlMappingNode();
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        using var reader = new StringReader(content);
        var stream = new YamlStream();
        stream.Load(reader);
        return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root ? root : new YamlMappingNode();
    }

    public static string Save(YamlMappingNode root)
    {
        var stream = new YamlStream();
        stream.Documents.Add(new YamlDocument(root));
        var writer = new StringWriter();
        stream.Save(writer, false);
        return StripDocumentEnd(writer.ToString());
    }

    /// <summary>YamlDotNet 会在文档末尾输出显式结束标记“...”，消费方（dsh 等）不期望它，剥掉最后一行该标记。</summary>
    private static string StripDocumentEnd(string text)
    {
        var trimmed = text.TrimEnd('\r', '\n');
        var lastLineStart = trimmed.LastIndexOf('\n');
        var lastLine = lastLineStart >= 0 ? trimmed[(lastLineStart + 1)..] : trimmed;
        if (lastLine != "...") return text;
        var cut = Math.Max(lastLineStart, 0);
        return trimmed[..cut].TrimEnd('\r') + Environment.NewLine;
    }
}
