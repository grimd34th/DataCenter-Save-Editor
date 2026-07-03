using System.Globalization;
using System.Text;

namespace DataCenterSaveEditor.Core;

/// <summary>
/// Parses the NRBF records used by BinaryFormatter without loading or constructing serialized CLR types.
/// Scalar writes are applied as byte patches over the original stream, preserving every unedited byte.
/// </summary>
public sealed class NrbfDocument
{
    private readonly byte[] _originalBytes;
    private readonly Dictionary<string, SaveNode> _nodesById;

    private NrbfDocument(byte[] bytes, SaveNode root, IReadOnlyList<SaveNode> topLevelNodes)
    {
        _originalBytes = bytes;
        Root = root;
        TopLevelNodes = topLevelNodes;
        _nodesById = topLevelNodes
            .SelectMany(node => node.DescendantsAndSelf())
            .ToDictionary(node => node.NodeId, StringComparer.Ordinal);
    }

    public SaveNode Root { get; }
    public IReadOnlyList<SaveNode> TopLevelNodes { get; }
    public IEnumerable<SaveNode> AllNodes => TopLevelNodes.SelectMany(node => node.DescendantsAndSelf());
    public IEnumerable<SaveNode> ScalarNodes => AllNodes.Where(node => node.Kind == SaveNodeKind.Scalar);
    public bool IsChanged => ScalarNodes.Any(node => node.IsChanged);

    public static NrbfDocument Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new Parser(bytes).Parse();
    }

    public static NrbfDocument Load(string path) => Parse(File.ReadAllBytes(path));

    public SaveNode? FindByPath(string path) =>
        AllNodes.FirstOrDefault(node => string.Equals(node.Path, path, StringComparison.Ordinal));

    public SaveNode? FindObjectById(int objectId) =>
        AllNodes.FirstOrDefault(node => node.ObjectId == objectId);

    public ScalarEditResult TrySetScalar(string nodeId, string value)
    {
        if (!_nodesById.TryGetValue(nodeId, out SaveNode? node) ||
            node.Kind != SaveNodeKind.Scalar || node.ScalarType is null)
        {
            return ScalarEditResult.Failure("The selected node is not an editable scalar.", nodeId);
        }

        if (!TryNormalize(node.ScalarType.Value, value, out string normalized, out string? error))
        {
            return ScalarEditResult.Failure(error ?? "The value is invalid.", nodeId);
        }

        List<ValidationIssue> issues = GetSemanticWarnings(node, normalized);
        node.Value = normalized;
        return new ScalarEditResult(true, issues);
    }

    public void RevertAll()
    {
        foreach (SaveNode node in ScalarNodes)
        {
            node.Value = node.OriginalValue;
        }
    }

    public IReadOnlyList<SaveChange> GetChanges() => ScalarNodes
        .Where(node => node.IsChanged)
        .Select(node => new SaveChange(node.Path, node.OriginalValue, node.Value))
        .ToArray();

    public byte[] ToArray()
    {
        SaveNode[] changes = ScalarNodes
            .Where(node => node.IsChanged)
            .OrderBy(node => node.ValueOffset)
            .ToArray();

        if (changes.Length == 0)
        {
            return _originalBytes.ToArray();
        }

        using MemoryStream output = new(_originalBytes.Length + 256);
        int cursor = 0;
        foreach (SaveNode node in changes)
        {
            if (node.ValueOffset < cursor || node.ValueOffset + node.ValueLength > _originalBytes.Length)
            {
                throw new InvalidDataException($"Overlapping or invalid edit at {node.Path}.");
            }

            output.Write(_originalBytes, cursor, node.ValueOffset - cursor);
            byte[] encoded = Encode(node.ScalarType!.Value, node.Value!);
            output.Write(encoded);
            cursor = node.ValueOffset + node.ValueLength;
        }

        output.Write(_originalBytes, cursor, _originalBytes.Length - cursor);
        return output.ToArray();
    }

    public IReadOnlyList<ValidationIssue> Validate()
    {
        List<ValidationIssue> issues = [];
        try
        {
            _ = Parse(ToArray());
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or DecoderFallbackException or OverflowException)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, $"The edited NRBF stream is invalid: {ex.Message}"));
            return issues;
        }

        foreach (SaveNode node in ScalarNodes.Where(node => node.IsChanged && node.Value is not null))
        {
            issues.AddRange(GetSemanticWarnings(node, node.Value!));
        }

        return issues;
    }

    private static bool TryNormalize(ScalarType type, string input, out string normalized, out string? error)
    {
        normalized = input;
        error = null;
        NumberStyles integer = NumberStyles.Integer;
        CultureInfo culture = ScalarFormatting.Culture;

        static bool Invalid(string expected, out string? message)
        {
            message = $"Enter a valid {expected} value.";
            return false;
        }

        switch (type)
        {
            case ScalarType.String:
                normalized = input;
                return true;
            case ScalarType.Boolean:
                if (!bool.TryParse(input, out bool boolean)) return Invalid("Boolean (true or false)", out error);
                normalized = boolean ? "true" : "false";
                return true;
            case ScalarType.Byte:
                if (!byte.TryParse(input, integer, culture, out byte u8)) return Invalid("8-bit unsigned integer", out error);
                normalized = u8.ToString(culture);
                return true;
            case ScalarType.SByte:
                if (!sbyte.TryParse(input, integer, culture, out sbyte i8)) return Invalid("8-bit integer", out error);
                normalized = i8.ToString(culture);
                return true;
            case ScalarType.Int16:
                if (!short.TryParse(input, integer, culture, out short i16)) return Invalid("16-bit integer", out error);
                normalized = i16.ToString(culture);
                return true;
            case ScalarType.UInt16:
                if (!ushort.TryParse(input, integer, culture, out ushort u16)) return Invalid("16-bit unsigned integer", out error);
                normalized = u16.ToString(culture);
                return true;
            case ScalarType.Int32:
                if (!int.TryParse(input, integer, culture, out int i32)) return Invalid("32-bit integer", out error);
                normalized = i32.ToString(culture);
                return true;
            case ScalarType.UInt32:
                if (!uint.TryParse(input, integer, culture, out uint u32)) return Invalid("32-bit unsigned integer", out error);
                normalized = u32.ToString(culture);
                return true;
            case ScalarType.Int64:
            case ScalarType.TimeSpan:
            case ScalarType.DateTime:
                if (!long.TryParse(input, integer, culture, out long i64)) return Invalid("64-bit integer", out error);
                normalized = i64.ToString(culture);
                return true;
            case ScalarType.UInt64:
                if (!ulong.TryParse(input, integer, culture, out ulong u64)) return Invalid("64-bit unsigned integer", out error);
                normalized = u64.ToString(culture);
                return true;
            case ScalarType.Single:
                if (!float.TryParse(input, NumberStyles.Float, culture, out float single) || !float.IsFinite(single))
                    return Invalid("finite single-precision number", out error);
                normalized = single.ToString("R", culture);
                return true;
            case ScalarType.Double:
                if (!double.TryParse(input, NumberStyles.Float, culture, out double dbl) || !double.IsFinite(dbl))
                    return Invalid("finite double-precision number", out error);
                normalized = dbl.ToString("R", culture);
                return true;
            case ScalarType.Decimal:
                if (!decimal.TryParse(input, NumberStyles.Number, culture, out decimal dec)) return Invalid("decimal number", out error);
                normalized = dec.ToString(culture);
                return true;
            case ScalarType.Char:
                if (input.Length != 1) return Invalid("single character", out error);
                normalized = input;
                return true;
            default:
                return Invalid(type.ToString(), out error);
        }
    }

    private static List<ValidationIssue> GetSemanticWarnings(SaveNode node, string value)
    {
        List<ValidationIssue> warnings = [];
        string lowerPath = node.Path.ToLowerInvariant();

        if ((lowerPath.EndsWith(".coins", StringComparison.Ordinal) ||
             lowerPath.EndsWith(".reputation", StringComparison.Ordinal) ||
             lowerPath.EndsWith(".wallprice", StringComparison.Ordinal)) &&
            decimal.TryParse(value, NumberStyles.Number, ScalarFormatting.Culture, out decimal amount) && amount < 0)
        {
            warnings.Add(new ValidationIssue(ValidationSeverity.Warning, "This progression value is negative and may not be accepted by the game.", node.NodeId));
        }

        if ((node.Name is "x" or "y" or "z" or "w") &&
            double.TryParse(value, NumberStyles.Float, ScalarFormatting.Culture, out double component) && Math.Abs(component) > 100_000)
        {
            warnings.Add(new ValidationIssue(ValidationSeverity.Warning, "This coordinate or rotation component is unusually large.", node.NodeId));
        }

        if (node.Name == "value__" && long.TryParse(value, NumberStyles.Integer, ScalarFormatting.Culture, out long enumValue) &&
            (enumValue < -1 || enumValue > 256))
        {
            warnings.Add(new ValidationIssue(ValidationSeverity.Warning, "This enum value is outside the usual range.", node.NodeId));
        }

        if (node.ScalarType == ScalarType.String && Encoding.UTF8.GetByteCount(value) > 4096)
        {
            warnings.Add(new ValidationIssue(ValidationSeverity.Warning, "This string is unusually large for a game-save field.", node.NodeId));
        }

        return warnings;
    }

    private static byte[] Encode(ScalarType type, string value)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, new UTF8Encoding(false, true), leaveOpen: true);
        CultureInfo culture = ScalarFormatting.Culture;
        switch (type)
        {
            case ScalarType.Boolean: writer.Write(bool.Parse(value)); break;
            case ScalarType.Byte: writer.Write(byte.Parse(value, culture)); break;
            case ScalarType.Char: writer.Write(value[0]); break;
            case ScalarType.Decimal: writer.Write(decimal.Parse(value, culture)); break;
            case ScalarType.Double: writer.Write(double.Parse(value, culture)); break;
            case ScalarType.Int16: writer.Write(short.Parse(value, culture)); break;
            case ScalarType.Int32: writer.Write(int.Parse(value, culture)); break;
            case ScalarType.Int64:
            case ScalarType.TimeSpan:
            case ScalarType.DateTime: writer.Write(long.Parse(value, culture)); break;
            case ScalarType.SByte: writer.Write(sbyte.Parse(value, culture)); break;
            case ScalarType.Single: writer.Write(float.Parse(value, culture)); break;
            case ScalarType.UInt16: writer.Write(ushort.Parse(value, culture)); break;
            case ScalarType.UInt32: writer.Write(uint.Parse(value, culture)); break;
            case ScalarType.UInt64: writer.Write(ulong.Parse(value, culture)); break;
            case ScalarType.String: writer.Write(value); break;
            default: throw new InvalidDataException($"Unsupported scalar type {type}.");
        }

        writer.Flush();
        return stream.ToArray();
    }

    private sealed class Parser
    {
        private const int MaxCollectionLength = 2_000_000;
        private const int MaxMembers = 100_000;
        private const int MaxDepth = 512;

        private readonly byte[] _bytes;
        private readonly MemoryStream _stream;
        private readonly BinaryReader _reader;
        private readonly Dictionary<int, ClassMetadata> _metadata = [];
        private readonly Dictionary<int, SaveNode> _objects = [];
        private readonly List<SaveNode> _topLevel = [];
        private int _nextNodeId;
        private int _rootId;

        internal Parser(byte[] bytes)
        {
            _bytes = bytes;
            _stream = new MemoryStream(bytes, writable: false);
            _reader = new BinaryReader(_stream, new UTF8Encoding(false, true), leaveOpen: false);
        }

        internal NrbfDocument Parse()
        {
            if (_reader.ReadByte() != (byte)RecordType.SerializedStreamHeader)
            {
                throw new InvalidDataException("The file does not start with an NRBF stream header.");
            }

            _rootId = _reader.ReadInt32();
            _ = _reader.ReadInt32(); // header id
            int major = _reader.ReadInt32();
            int minor = _reader.ReadInt32();
            if (major != 1 || minor != 0)
            {
                throw new InvalidDataException($"Unsupported NRBF stream version {major}.{minor}.");
            }

            bool ended = false;
            while (_stream.Position < _stream.Length)
            {
                RecordType type = ReadRecordType();
                if (type == RecordType.MessageEnd)
                {
                    ended = true;
                    break;
                }

                if (type == RecordType.BinaryLibrary)
                {
                    ReadLibrary();
                    continue;
                }

                SaveNode node = ReadRecord(type, "$", "$root", 0);
                _topLevel.Add(node);
            }

            if (!ended || _stream.Position != _stream.Length)
            {
                throw new InvalidDataException("The NRBF stream is missing its final MessageEnd record or contains trailing data.");
            }

            if (!_objects.TryGetValue(_rootId, out SaveNode? root))
            {
                throw new InvalidDataException($"Root object ID {_rootId} was not found.");
            }

            foreach (SaveNode reference in _topLevel.SelectMany(node => node.DescendantsAndSelf()).Where(node => node.Kind == SaveNodeKind.Reference))
            {
                if (reference.ReferencedObjectId is int id && _objects.TryGetValue(id, out SaveNode? target))
                {
                    reference.ReferencedPath = target.Path;
                    reference.SerializedType = target.SerializedType;
                }
            }

            return new NrbfDocument(_bytes.ToArray(), root, _topLevel.ToArray());
        }

        private SaveNode ReadValue(string path, string name, int depth)
        {
            while (true)
            {
                RecordType type = ReadRecordType();
                if (type == RecordType.BinaryLibrary)
                {
                    ReadLibrary();
                    continue;
                }

                return ReadRecord(type, path, name, depth);
            }
        }

        private SaveNode ReadRecord(RecordType type, string path, string name, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidDataException("The NRBF object graph exceeds the recursion limit.");
            }

            return type switch
            {
                RecordType.ClassWithId => ReadClassWithId(path, name, depth),
                RecordType.SystemClassWithMembers => ReadClassWithMembers(path, name, depth, hasLibrary: false),
                RecordType.ClassWithMembers => ReadClassWithMembers(path, name, depth, hasLibrary: true),
                RecordType.SystemClassWithMembersAndTypes => ReadClassWithTypes(path, name, depth, hasLibrary: false),
                RecordType.ClassWithMembersAndTypes => ReadClassWithTypes(path, name, depth, hasLibrary: true),
                RecordType.BinaryObjectString => ReadObjectString(path, name),
                RecordType.BinaryArray => ReadBinaryArray(path, name, depth),
                RecordType.MemberPrimitiveTyped => ReadTypedPrimitive(path, name),
                RecordType.MemberReference => ReadReference(path, name),
                RecordType.ObjectNull => NewNode(name, path, "null", SaveNodeKind.Null),
                RecordType.ObjectNullMultiple256 => ReadNullRun(path, name, _reader.ReadByte()),
                RecordType.ObjectNullMultiple => ReadNullRun(path, name, ReadBoundedCount("null run")),
                RecordType.ArraySinglePrimitive => ReadSinglePrimitiveArray(path, name),
                RecordType.ArraySingleObject => ReadSingleRecordArray(path, name, depth, "System.Object[]"),
                RecordType.ArraySingleString => ReadSingleRecordArray(path, name, depth, "System.String[]"),
                _ => throw new InvalidDataException($"Unsupported NRBF record type {(byte)type} at offset {_stream.Position - 1}.")
            };
        }

        private SaveNode ReadClassWithTypes(string path, string name, int depth, bool hasLibrary)
        {
            int objectId = _reader.ReadInt32();
            string className = _reader.ReadString();
            int memberCount = ReadBoundedCount("class member count", MaxMembers);
            string[] names = ReadStrings(memberCount);
            BinaryTypeInfo[] types = ReadTypeInfo(memberCount);
            int libraryId = hasLibrary ? _reader.ReadInt32() : 0;
            ClassMetadata metadata = new(objectId, className, names, types, libraryId);
            _metadata[objectId] = metadata;
            return ReadClassValues(objectId, metadata, path, name, depth);
        }

        private SaveNode ReadClassWithMembers(string path, string name, int depth, bool hasLibrary)
        {
            int objectId = _reader.ReadInt32();
            string className = _reader.ReadString();
            int memberCount = ReadBoundedCount("class member count", MaxMembers);
            string[] names = ReadStrings(memberCount);
            int libraryId = hasLibrary ? _reader.ReadInt32() : 0;
            BinaryTypeInfo[] types = Enumerable.Repeat(new BinaryTypeInfo(BinaryType.Object, null, null), memberCount).ToArray();
            ClassMetadata metadata = new(objectId, className, names, types, libraryId);
            _metadata[objectId] = metadata;
            return ReadClassValues(objectId, metadata, path, name, depth);
        }

        private SaveNode ReadClassWithId(string path, string name, int depth)
        {
            int objectId = _reader.ReadInt32();
            int metadataId = _reader.ReadInt32();
            if (!_metadata.TryGetValue(metadataId, out ClassMetadata? metadata))
            {
                throw new InvalidDataException($"ClassWithId references unknown metadata ID {metadataId}.");
            }

            return ReadClassValues(objectId, metadata, path, name, depth);
        }

        private SaveNode ReadClassValues(int objectId, ClassMetadata metadata, string path, string name, int depth)
        {
            SaveNode node = NewNode(name, path, metadata.Name, SaveNodeKind.Object, objectId);
            RegisterObject(objectId, node);
            for (int i = 0; i < metadata.MemberNames.Length; i++)
            {
                string memberName = metadata.MemberNames[i];
                string childPath = path == "$" ? $"$.{memberName}" : $"{path}.{memberName}";
                BinaryTypeInfo info = metadata.MemberTypes[i];
                SaveNode child = info.BinaryType == BinaryType.Primitive
                    ? ReadPrimitive(childPath, memberName, info.PrimitiveType ?? throw new InvalidDataException("Primitive member lacks type information."))
                    : ReadValue(childPath, memberName, depth + 1);
                node.AddChild(child);
            }

            return node;
        }

        private SaveNode ReadObjectString(string path, string name)
        {
            int objectId = _reader.ReadInt32();
            int offset = CheckedPosition();
            string value = _reader.ReadString();
            SaveNode node = NewScalar(name, path, ScalarType.String, value, offset, CheckedPosition() - offset, objectId);
            RegisterObject(objectId, node);
            return node;
        }

        private SaveNode ReadTypedPrimitive(string path, string name)
        {
            ScalarType primitive = ReadPrimitiveType();
            return ReadPrimitive(path, name, primitive);
        }

        private SaveNode ReadReference(string path, string name)
        {
            int id = _reader.ReadInt32();
            return new SaveNode(NextNodeId(), name, path, "reference", SaveNodeKind.Reference)
            {
                ReferencedObjectId = id
            };
        }

        private SaveNode ReadNullRun(string path, string name, int count)
        {
            if (count <= 0 || count > MaxCollectionLength)
            {
                throw new InvalidDataException($"Invalid null run length {count}.");
            }

            SaveNode node = NewNode(name, path, "null", SaveNodeKind.Null);
            node.NullRunLength = count;
            return node;
        }

        private SaveNode ReadSinglePrimitiveArray(string path, string name)
        {
            int objectId = _reader.ReadInt32();
            int length = ReadBoundedCount("array length");
            ScalarType primitive = ReadPrimitiveType();
            SaveNode node = NewNode(name, path, $"{primitive}[]", SaveNodeKind.Array, objectId);
            RegisterObject(objectId, node);
            for (int i = 0; i < length; i++)
            {
                node.AddChild(ReadPrimitive($"{path}[{i}]", $"[{i}]", primitive));
            }

            return node;
        }

        private SaveNode ReadSingleRecordArray(string path, string name, int depth, string typeName)
        {
            int objectId = _reader.ReadInt32();
            int length = ReadBoundedCount("array length");
            SaveNode node = NewNode(name, path, typeName, SaveNodeKind.Array, objectId);
            RegisterObject(objectId, node);
            ReadRecordArrayElements(node, path, length, depth);
            return node;
        }

        private SaveNode ReadBinaryArray(string path, string name, int depth)
        {
            int objectId = _reader.ReadInt32();
            BinaryArrayType arrayType = (BinaryArrayType)_reader.ReadByte();
            if (!Enum.IsDefined(arrayType))
            {
                throw new InvalidDataException($"Unknown binary array type {(byte)arrayType}.");
            }

            int rank = ReadBoundedCount("array rank", 32);
            int[] lengths = new int[rank];
            long total = 1;
            for (int i = 0; i < rank; i++)
            {
                lengths[i] = ReadBoundedCount("array dimension");
                total = checked(total * lengths[i]);
                if (total > MaxCollectionLength) throw new InvalidDataException("Array contains too many elements.");
            }

            if (arrayType is BinaryArrayType.SingleOffset or BinaryArrayType.JaggedOffset or BinaryArrayType.RectangularOffset)
            {
                for (int i = 0; i < rank; i++) _ = _reader.ReadInt32();
            }

            BinaryTypeInfo elementInfo = ReadTypeInfo();
            string elementName = elementInfo.TypeName ?? elementInfo.PrimitiveType?.ToString() ?? elementInfo.BinaryType.ToString();
            SaveNode node = NewNode(name, path, $"{elementName}[{new string(',', Math.Max(0, rank - 1))}]", SaveNodeKind.Array, objectId);
            RegisterObject(objectId, node);

            int count = checked((int)total);
            if (elementInfo.BinaryType == BinaryType.Primitive)
            {
                ScalarType primitive = elementInfo.PrimitiveType ?? throw new InvalidDataException("Primitive array lacks its element type.");
                for (int i = 0; i < count; i++)
                {
                    node.AddChild(ReadPrimitive($"{path}[{i}]", $"[{i}]", primitive));
                }
            }
            else
            {
                ReadRecordArrayElements(node, path, count, depth);
            }

            return node;
        }

        private void ReadRecordArrayElements(SaveNode array, string path, int count, int depth)
        {
            int index = 0;
            while (index < count)
            {
                SaveNode item = ReadValue($"{path}[{index}]", $"[{index}]", depth + 1);
                array.AddChild(item);
                int run = item.Kind == SaveNodeKind.Null ? item.NullRunLength : 1;
                if (index + run > count)
                {
                    throw new InvalidDataException("A compressed null run exceeds its containing array.");
                }

                for (int i = 1; i < run; i++)
                {
                    array.AddChild(NewNode($"[{index + i}]", $"{path}[{index + i}]", "null", SaveNodeKind.Null));
                }

                index += run;
            }
        }

        private SaveNode ReadPrimitive(string path, string name, ScalarType type)
        {
            int offset = CheckedPosition();
            object value = type switch
            {
                ScalarType.Boolean => _reader.ReadBoolean(),
                ScalarType.Byte => _reader.ReadByte(),
                ScalarType.Char => _reader.ReadChar(),
                ScalarType.Decimal => _reader.ReadDecimal(),
                ScalarType.Double => _reader.ReadDouble(),
                ScalarType.Int16 => _reader.ReadInt16(),
                ScalarType.Int32 => _reader.ReadInt32(),
                ScalarType.Int64 => _reader.ReadInt64(),
                ScalarType.SByte => _reader.ReadSByte(),
                ScalarType.Single => _reader.ReadSingle(),
                ScalarType.TimeSpan => _reader.ReadInt64(),
                ScalarType.DateTime => _reader.ReadInt64(),
                ScalarType.UInt16 => _reader.ReadUInt16(),
                ScalarType.UInt32 => _reader.ReadUInt32(),
                ScalarType.UInt64 => _reader.ReadUInt64(),
                ScalarType.String => _reader.ReadString(),
                _ => throw new InvalidDataException($"Unsupported primitive type {(byte)type}.")
            };

            string text = value switch
            {
                bool boolean => boolean ? "true" : "false",
                float single => single.ToString("R", ScalarFormatting.Culture),
                double dbl => dbl.ToString("R", ScalarFormatting.Culture),
                IFormattable formattable => formattable.ToString(null, ScalarFormatting.Culture),
                _ => value.ToString() ?? string.Empty
            };
            return NewScalar(name, path, type, text, offset, CheckedPosition() - offset);
        }

        private BinaryTypeInfo[] ReadTypeInfo(int count)
        {
            BinaryType[] binaryTypes = new BinaryType[count];
            for (int i = 0; i < count; i++)
            {
                binaryTypes[i] = ReadBinaryType();
            }

            BinaryTypeInfo[] result = new BinaryTypeInfo[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = ReadTypeInfo(binaryTypes[i]);
            }

            return result;
        }

        private BinaryTypeInfo ReadTypeInfo() => ReadTypeInfo(ReadBinaryType());

        private BinaryTypeInfo ReadTypeInfo(BinaryType type) => type switch
        {
            BinaryType.Primitive => new(type, ReadPrimitiveType(), null),
            BinaryType.String => new(type, null, "System.String"),
            BinaryType.Object => new(type, null, "System.Object"),
            BinaryType.SystemClass => new(type, null, _reader.ReadString()),
            BinaryType.Class => new(type, null, ReadClassTypeInfo()),
            BinaryType.ObjectArray => new(type, null, "System.Object[]"),
            BinaryType.StringArray => new(type, null, "System.String[]"),
            BinaryType.PrimitiveArray => new(type, ReadPrimitiveType(), null),
            _ => throw new InvalidDataException($"Unsupported binary type {(byte)type}.")
        };

        private string ReadClassTypeInfo()
        {
            string typeName = _reader.ReadString();
            _ = _reader.ReadInt32();
            return typeName;
        }

        private void ReadLibrary()
        {
            _ = _reader.ReadInt32();
            _ = _reader.ReadString();
        }

        private string[] ReadStrings(int count)
        {
            string[] values = new string[count];
            for (int i = 0; i < count; i++) values[i] = _reader.ReadString();
            return values;
        }

        private RecordType ReadRecordType()
        {
            byte value = _reader.ReadByte();
            if (!Enum.IsDefined((RecordType)value))
            {
                throw new InvalidDataException($"Unknown NRBF record type {value} at offset {_stream.Position - 1}.");
            }

            return (RecordType)value;
        }

        private BinaryType ReadBinaryType()
        {
            byte value = _reader.ReadByte();
            if (value > (byte)BinaryType.PrimitiveArray)
            {
                throw new InvalidDataException($"Unknown NRBF binary type {value}.");
            }

            return (BinaryType)value;
        }

        private ScalarType ReadPrimitiveType()
        {
            byte value = _reader.ReadByte();
            if (!Enum.IsDefined((ScalarType)value))
            {
                throw new InvalidDataException($"Unknown or unsupported NRBF primitive type {value}.");
            }

            return (ScalarType)value;
        }

        private int ReadBoundedCount(string description, int maximum = MaxCollectionLength)
        {
            int value = _reader.ReadInt32();
            if (value < 0 || value > maximum)
            {
                throw new InvalidDataException($"Invalid {description}: {value}.");
            }

            return value;
        }

        private int CheckedPosition() => checked((int)_stream.Position);

        private SaveNode NewNode(string name, string path, string type, SaveNodeKind kind, int? objectId = null) =>
            new(NextNodeId(), name, path, type, kind, objectId);

        private SaveNode NewScalar(string name, string path, ScalarType type, string value, int offset, int length, int? objectId = null) =>
            new(NextNodeId(), name, path, type.ToString(), SaveNodeKind.Scalar, objectId)
            {
                ScalarType = type,
                Value = value,
                OriginalValue = value,
                ValueOffset = offset,
                ValueLength = length
            };

        private string NextNodeId() => $"n{++_nextNodeId}";

        private void RegisterObject(int objectId, SaveNode node)
        {
            if (!_objects.TryAdd(objectId, node))
            {
                throw new InvalidDataException($"Duplicate NRBF object ID {objectId}.");
            }
        }

        private sealed record ClassMetadata(int ObjectId, string Name, string[] MemberNames, BinaryTypeInfo[] MemberTypes, int LibraryId);
        private sealed record BinaryTypeInfo(BinaryType BinaryType, ScalarType? PrimitiveType, string? TypeName);
    }

    private enum RecordType : byte
    {
        SerializedStreamHeader = 0,
        ClassWithId = 1,
        SystemClassWithMembers = 2,
        ClassWithMembers = 3,
        SystemClassWithMembersAndTypes = 4,
        ClassWithMembersAndTypes = 5,
        BinaryObjectString = 6,
        BinaryArray = 7,
        MemberPrimitiveTyped = 8,
        MemberReference = 9,
        ObjectNull = 10,
        MessageEnd = 11,
        BinaryLibrary = 12,
        ObjectNullMultiple256 = 13,
        ObjectNullMultiple = 14,
        ArraySinglePrimitive = 15,
        ArraySingleObject = 16,
        ArraySingleString = 17,
        MethodCall = 21,
        MethodReturn = 22
    }

    private enum BinaryType : byte
    {
        Primitive = 0,
        String = 1,
        Object = 2,
        SystemClass = 3,
        Class = 4,
        ObjectArray = 5,
        StringArray = 6,
        PrimitiveArray = 7
    }

    private enum BinaryArrayType : byte
    {
        Single = 0,
        Jagged = 1,
        Rectangular = 2,
        SingleOffset = 3,
        JaggedOffset = 4,
        RectangularOffset = 5
    }
}
