using System.Collections.ObjectModel;
using System.Globalization;

namespace DataCenterSaveEditor.Core;

public sealed record SavePair(string BaseName, string SavePath, string MetaPath)
{
    public static SavePair FromSavePath(string savePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savePath);
        string fullPath = Path.GetFullPath(savePath);
        if (!string.Equals(Path.GetExtension(fullPath), ".save", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Select a .save file.", nameof(savePath));
        }

        return new SavePair(
            Path.GetFileNameWithoutExtension(fullPath),
            fullPath,
            Path.ChangeExtension(fullPath, ".meta"));
    }
}

public enum SaveNodeKind
{
    Object,
    Array,
    Scalar,
    Reference,
    Null
}

public enum ScalarType : byte
{
    Boolean = 1,
    Byte = 2,
    Char = 3,
    Decimal = 5,
    Double = 6,
    Int16 = 7,
    Int32 = 8,
    Int64 = 9,
    SByte = 10,
    Single = 11,
    TimeSpan = 12,
    DateTime = 13,
    UInt16 = 14,
    UInt32 = 15,
    UInt64 = 16,
    String = 18
}

public sealed class SaveNode
{
    private readonly List<SaveNode> _children = [];

    internal SaveNode(
        string nodeId,
        string name,
        string path,
        string serializedType,
        SaveNodeKind kind,
        int? objectId = null)
    {
        NodeId = nodeId;
        Name = name;
        Path = path;
        SerializedType = serializedType;
        Kind = kind;
        ObjectId = objectId;
    }

    public string NodeId { get; }
    public string Name { get; }
    public string Path { get; }
    public string SerializedType { get; internal set; }
    public SaveNodeKind Kind { get; }
    public int? ObjectId { get; }
    public int? ReferencedObjectId { get; internal set; }
    public string? ReferencedPath { get; internal set; }
    public ScalarType? ScalarType { get; internal set; }
    public string? Value { get; internal set; }
    public string? OriginalValue { get; internal set; }
    public IReadOnlyList<SaveNode> Children => new ReadOnlyCollection<SaveNode>(_children);
    public bool IsChanged => Kind == SaveNodeKind.Scalar && !string.Equals(Value, OriginalValue, StringComparison.Ordinal);

    internal int ValueOffset { get; set; }
    internal int ValueLength { get; set; }
    internal int NullRunLength { get; set; } = 1;
    internal void AddChild(SaveNode child) => _children.Add(child);

    public IEnumerable<SaveNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (SaveNode child in _children)
        {
            foreach (SaveNode descendant in child.DescendantsAndSelf())
            {
                yield return descendant;
            }
        }
    }

    public override string ToString() => Kind == SaveNodeKind.Scalar
        ? $"{Name}: {Value}"
        : $"{Name} ({SerializedType})";
}

public enum ValidationSeverity
{
    Warning,
    Error
}

public sealed record ValidationIssue(ValidationSeverity Severity, string Message, string? NodeId = null);

public sealed record SaveChange(string Path, string? OldValue, string? NewValue);

public sealed record ScalarEditResult(bool Success, IReadOnlyList<ValidationIssue> Issues)
{
    public static ScalarEditResult Failure(string message, string? nodeId = null) =>
        new(false, [new ValidationIssue(ValidationSeverity.Error, message, nodeId)]);
}

internal static class ScalarFormatting
{
    public static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
}
