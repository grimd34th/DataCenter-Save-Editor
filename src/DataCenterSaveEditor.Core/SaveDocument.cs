using System.Globalization;
using System.Text.Json;

namespace DataCenterSaveEditor.Core;

public sealed class SaveDocument
{
    public const int SupportedVersion = 8;

    private readonly string _originalMetadataName;
    private readonly SaveNode? _nameMember;
    private readonly SaveNode? _nameNode;
    private readonly SaveNode? _versionNode;
    private string _metadataName;

    private SaveDocument(SavePair pair, NrbfDocument binary, SaveMetadata metadata)
    {
        Pair = pair;
        Binary = binary;
        MetadataVersion = metadata.Version;
        _metadataName = metadata.NameOfSave ?? string.Empty;
        _originalMetadataName = _metadataName;
        _nameMember = binary.Root.Children.FirstOrDefault(node => node.Name == "nameOfSave");
        _nameNode = ResolveStringMember(binary, _nameMember);
        _versionNode = binary.Root.Children.FirstOrDefault(node => node.Name == "version" && node.Kind == SaveNodeKind.Scalar);
    }

    public SavePair Pair { get; }
    public NrbfDocument Binary { get; }
    public int MetadataVersion { get; }
    public string Name => _metadataName;
    public SaveMetadata Metadata => new(MetadataVersion, _metadataName);
    public bool IsChanged => Binary.IsChanged || !string.Equals(_metadataName, _originalMetadataName, StringComparison.Ordinal);
    public bool CanWrite => !Validate().Any(issue => issue.Severity == ValidationSeverity.Error);

    public static SaveDocument Load(SavePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (!File.Exists(pair.SavePath)) throw new FileNotFoundException("The .save file was not found.", pair.SavePath);
        if (!File.Exists(pair.MetaPath)) throw new FileNotFoundException("The matching .meta file was not found.", pair.MetaPath);
        return Load(pair, File.ReadAllBytes(pair.SavePath), File.ReadAllBytes(pair.MetaPath));
    }

    internal static SaveDocument Load(SavePair pair, byte[] saveBytes, byte[] metadataBytes)
    {
        try
        {
            return new SaveDocument(pair, NrbfDocument.Parse(saveBytes), SaveMetadataSerializer.Parse(metadataBytes));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The metadata file is not valid JSON.", ex);
        }
    }

    public ScalarEditResult TrySetScalar(string nodeId, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(value);
        if (_versionNode?.NodeId == nodeId)
        {
            return ScalarEditResult.Failure("The save format version cannot be edited.", nodeId);
        }

        ScalarEditResult result = Binary.TrySetScalar(nodeId, value);
        if (result.Success && _nameNode?.NodeId == nodeId) _metadataName = _nameNode.Value ?? string.Empty;
        return result;
    }

    public ScalarEditResult TrySetName(string value)
    {
        if (_nameNode is null)
        {
            return ScalarEditResult.Failure(_nameMember?.Kind == SaveNodeKind.Null
                ? "This autosave stores nameOfSave as null; its metadata name is preserved but cannot be edited."
                : "The SaveData root does not contain an editable nameOfSave field.");
        }

        return TrySetScalar(_nameNode.NodeId, value);
    }

    public void RevertAll()
    {
        Binary.RevertAll();
        _metadataName = _originalMetadataName;
    }

    public IReadOnlyList<SaveChange> GetChanges()
    {
        List<SaveChange> changes = [.. Binary.GetChanges()];
        if (!string.Equals(_metadataName, _originalMetadataName, StringComparison.Ordinal))
        {
            changes.Add(new SaveChange(".meta.nameOfSave", _originalMetadataName, _metadataName));
        }

        return changes;
    }

    public byte[] CreateSaveBytes() => Binary.ToArray();
    public byte[] CreateMetadataBytes() => SaveMetadataSerializer.Serialize(Metadata);
    public IReadOnlyList<ValidationIssue> Validate() => SaveValidator.Validate(this);

    private static SaveNode? ResolveStringMember(NrbfDocument binary, SaveNode? member)
    {
        if (member?.Kind == SaveNodeKind.Scalar) return member;
        if (member?.Kind == SaveNodeKind.Reference && member.ReferencedObjectId is int objectId)
        {
            SaveNode? target = binary.FindObjectById(objectId);
            return target?.Kind == SaveNodeKind.Scalar ? target : null;
        }

        return null;
    }

    internal IReadOnlyList<ValidationIssue> ValidateCore()
    {
        List<ValidationIssue> issues = [.. Binary.Validate()];
        if (!string.Equals(Binary.Root.SerializedType, "SaveData", StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, $"The NRBF root is '{Binary.Root.SerializedType}', not SaveData."));
        }

        if (_versionNode?.ScalarType != ScalarType.Int32 ||
            !int.TryParse(_versionNode.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int binaryVersion))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "The SaveData root does not contain a valid Int32 version field.", _versionNode?.NodeId));
        }
        else
        {
            if (binaryVersion != MetadataVersion)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, $"The binary version ({binaryVersion}) does not match the metadata version ({MetadataVersion}).", _versionNode.NodeId));
            }

            if (binaryVersion != SupportedVersion)
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Error, $"Save version {binaryVersion} is read-only; only version {SupportedVersion} can be written.", _versionNode.NodeId));
            }
        }

        if (MetadataVersion != SupportedVersion)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, $"Metadata version {MetadataVersion} is read-only; only version {SupportedVersion} can be written."));
        }

        if (_nameMember is null)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "The SaveData root does not contain a nameOfSave field."));
        }
        else if (_nameMember.Kind == SaveNodeKind.Null)
        {
            // Data Center autosaves intentionally store a null binary name and keep their display name in .meta.
        }
        else if (_nameNode?.ScalarType != ScalarType.String)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "The SaveData root contains an unsupported nameOfSave representation.", _nameMember.NodeId));
        }
        else if (!string.Equals(_nameNode.Value, _metadataName, StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "nameOfSave differs between the .save and .meta files.", _nameNode.NodeId));
        }

        return issues;
    }
}

public static class SaveValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(SaveDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.ValidateCore();
    }
}
