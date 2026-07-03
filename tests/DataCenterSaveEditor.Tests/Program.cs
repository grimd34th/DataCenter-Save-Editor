using System.Security.Cryptography;
using System.Text;
using DataCenterSaveEditor.Core;

namespace DataCenterSaveEditor.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--inspect") return Inspect(args[1]);

        (string Name, Action Test)[] tests =
        [
            ("synthetic scalar parse and no-op round trip", TestScalarParseAndNoOp),
            ("integer, float, string, Boolean, enum, and vector edits", TestScalarEdits),
            ("arrays, nulls, and references", TestArraysNullsAndReferences),
            ("malformed stream rejection", TestMalformedStream),
            ("metadata synchronization and version protection", TestMetadataSynchronization),
            ("autosaves with null binary names remain writable", TestNullAutosaveName),
            ("unsupported versions are read-only", TestUnsupportedVersion),
            ("save discovery requires paired files", TestSaveDiscovery),
            ("successful commit creates a two-file backup", TestCommitAndBackup),
            ("partial commit failure rolls both files back", TestRollback),
            ("running game blocks writes", TestGameRunningBlock),
            ("semantic warnings require confirmation", TestWarningConfirmation)
        ];

        int failures = 0;
        foreach ((string name, Action test) in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  {name}\n      {ex.Message}");
            }
        }

        string? integrationPath = Environment.GetEnvironmentVariable("DATACENTER_TEST_SAVE");
        if (!string.IsNullOrWhiteSpace(integrationPath))
        {
            try
            {
                TestRealSave(integrationPath);
                Console.WriteLine("PASS  external real-save parse and no-op round trip");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  external real-save parse and no-op round trip\n      {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("SKIP  external real-save test (set DATACENTER_TEST_SAVE)");
        }

        Console.WriteLine($"\n{tests.Length + (integrationPath is null ? 0 : 1) - failures} passed, {failures} failed.");
        return failures == 0 ? 0 : 1;
    }

    private static int Inspect(string path)
    {
        byte[] input = File.ReadAllBytes(path);
        NrbfDocument document = NrbfDocument.Parse(input);
        byte[] output = document.ToArray();
        Console.WriteLine($"RootType={document.Root.SerializedType}");
        Console.WriteLine($"Nodes={document.AllNodes.Count()}");
        Console.WriteLine($"Scalars={document.ScalarNodes.Count()}");
        Console.WriteLine($"ByteIdentical={input.AsSpan().SequenceEqual(output)}");
        Console.WriteLine($"OutputLength={output.Length}");
        return 0;
    }

    private static void TestScalarParseAndNoOp()
    {
        byte[] bytes = SyntheticNrbf.CreateScalarSave();
        NrbfDocument document = NrbfDocument.Parse(bytes);
        Equal("SaveData", document.Root.SerializedType);
        Equal("8", RequiredNode(document, "$.version").Value);
        Equal("Original", RequiredNode(document, "$.nameOfSave").Value);
        True(bytes.AsSpan().SequenceEqual(document.ToArray()), "No-op serialization changed bytes.");
        Equal(0, document.Validate().Count);
    }

    private static void TestScalarEdits()
    {
        NrbfDocument document = NrbfDocument.Parse(SyntheticNrbf.CreateScalarSave());
        Set(document, "$.version", "8");
        Set(document, "$.nameOfSave", "Edited save with a longer name");
        Set(document, "$.coins", "12345.5");
        Set(document, "$.isUnlocked", "false");
        Set(document, "$.mode.value__", "7");
        Set(document, "$.position.x", "-42.25");

        NrbfDocument reparsed = NrbfDocument.Parse(document.ToArray());
        Equal("Edited save with a longer name", RequiredNode(reparsed, "$.nameOfSave").Value);
        Equal("12345.5", RequiredNode(reparsed, "$.coins").Value);
        Equal("false", RequiredNode(reparsed, "$.isUnlocked").Value);
        Equal("7", RequiredNode(reparsed, "$.mode.value__").Value);
        Equal("-42.25", RequiredNode(reparsed, "$.position.x").Value);
        Equal("2", RequiredNode(reparsed, "$.position.y").Value);

        ScalarEditResult overflow = document.TrySetScalar(RequiredNode(document, "$.mode.value__").NodeId, "999999999999");
        True(!overflow.Success, "Int32 overflow was accepted.");
        ScalarEditResult nonFinite = document.TrySetScalar(RequiredNode(document, "$.coins").NodeId, "NaN");
        True(!nonFinite.Success, "A non-finite float was accepted.");

        document.RevertAll();
        True(SyntheticNrbf.CreateScalarSave().AsSpan().SequenceEqual(document.ToArray()), "Revert did not restore original bytes.");
    }

    private static void TestArraysNullsAndReferences()
    {
        NrbfDocument document = NrbfDocument.Parse(SyntheticNrbf.CreateArraySave());
        Equal("20", RequiredNode(document, "$.numbers[1]").Value);
        SaveNode names = document.FindByPath("$.names") ?? throw new Exception("String array was not parsed.");
        Equal(3, names.Children.Count);
        Equal(SaveNodeKind.Null, names.Children[1].Kind);
        Equal(SaveNodeKind.Reference, names.Children[2].Kind);
        Equal(names.Children[0].ObjectId, names.Children[2].ReferencedObjectId);
        Equal(names.Children[0].Path, names.Children[2].ReferencedPath);
        Set(document, "$.numbers[1]", "99");
        Equal("99", RequiredNode(NrbfDocument.Parse(document.ToArray()), "$.numbers[1]").Value);
    }

    private static void TestMalformedStream()
    {
        byte[] valid = SyntheticNrbf.CreateScalarSave();
        Throws<InvalidDataException>(() => NrbfDocument.Parse(valid[..^1]));
        byte[] badRecord = valid.ToArray();
        badRecord[17] = 255;
        Throws<InvalidDataException>(() => NrbfDocument.Parse(badRecord));
    }

    private static void TestMetadataSynchronization()
    {
        WithSavePair(8, "Original", (pair, _) =>
        {
            SaveDocument document = SaveDocument.Load(pair);
            True(document.CanWrite, "A valid version-8 document was not writable.");
            True(document.TrySetName("Synchronized").Success, "Save name edit failed.");
            string metadata = Encoding.UTF8.GetString(document.CreateMetadataBytes());
            True(metadata.Contains("\"nameOfSave\":\"Synchronized\"", StringComparison.Ordinal), "Metadata name was not synchronized.");
            NrbfDocument binary = NrbfDocument.Parse(document.CreateSaveBytes());
            Equal("Synchronized", RequiredNode(binary, "$.nameOfSave").Value);
            True(document.GetChanges().Any(change => change.Path == ".meta.nameOfSave"), "Metadata diff was not reported.");

            ScalarEditResult versionEdit = document.TrySetScalar(RequiredNode(document.Binary, "$.version").NodeId, "9");
            True(!versionEdit.Success, "The format version was editable.");
            document.RevertAll();
            True(!document.IsChanged, "Revert did not clear document changes.");
        });
    }

    private static void TestNullAutosaveName()
    {
        WithSavePair(8, "name", (pair, _) =>
        {
            SaveDocument document = SaveDocument.Load(pair);
            SaveNode nameMember = document.Binary.Root.Children.Single(node => node.Name == "nameOfSave");
            Equal(SaveNodeKind.Null, nameMember.Kind);
            True(document.CanWrite, "A game-produced autosave with a null binary name was not writable.");
            True(!document.TrySetName("renamed").Success, "A null autosave name was structurally replaced.");

            Set(document, "$.coins", "25");
            SaveCommitResult result = new SaveCommitService(() => false).Commit(document, warningsConfirmed: true);
            True(result.Success, result.Message ?? "Autosave commit failed.");

            SaveDocument reloaded = SaveDocument.Load(pair);
            SaveNode reloadedName = reloaded.Binary.Root.Children.Single(node => node.Name == "nameOfSave");
            Equal(SaveNodeKind.Null, reloadedName.Kind);
            Equal("name", reloaded.Name);
            Equal("25", RequiredNode(reloaded.Binary, "$.coins").Value);
            True(reloaded.CanWrite, "The committed autosave did not validate.");
        }, binaryName: null);
    }

    private static void TestUnsupportedVersion()
    {
        WithSavePair(9, "Original", (pair, _) =>
        {
            SaveDocument document = SaveDocument.Load(pair);
            True(!document.CanWrite, "An unsupported version was writable.");
            True(document.Validate().Any(issue => issue.Severity == ValidationSeverity.Error && issue.Message.Contains("read-only", StringComparison.Ordinal)),
                "Unsupported version diagnostic was missing.");
        });
    }

    private static void TestSaveDiscovery()
    {
        string directory = NewTemporaryDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "paired.save"), SyntheticNrbf.CreateScalarSave());
            File.WriteAllText(Path.Combine(directory, "paired.meta"), Metadata(8, "Original"));
            File.WriteAllBytes(Path.Combine(directory, "orphan.save"), SyntheticNrbf.CreateScalarSave());
            IReadOnlyList<SavePair> pairs = SavePairLocator.Discover(directory);
            Equal(1, pairs.Count);
            Equal("paired", pairs[0].BaseName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TestCommitAndBackup()
    {
        WithSavePair(8, "Original", (pair, directory) =>
        {
            byte[] originalSave = File.ReadAllBytes(pair.SavePath);
            byte[] originalMeta = File.ReadAllBytes(pair.MetaPath);
            SaveDocument document = SaveDocument.Load(pair);
            Set(document, "$.coins", "500.25");
            SaveCommitResult result = new SaveCommitService(() => false).Commit(document, warningsConfirmed: true);
            True(result.Success, result.Message ?? "Commit failed.");
            True(result.BackupDirectory is not null && Directory.Exists(result.BackupDirectory), "Backup directory was not created.");
            True(originalSave.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(result.BackupDirectory!, Path.GetFileName(pair.SavePath)))), "Backup .save differs.");
            True(originalMeta.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(result.BackupDirectory!, Path.GetFileName(pair.MetaPath)))), "Backup .meta differs.");
            SaveDocument reloaded = SaveDocument.Load(pair);
            Equal("500.25", RequiredNode(reloaded.Binary, "$.coins").Value);
            True(!Directory.EnumerateFiles(directory, ".*.tmp").Any(), "Temporary files were left behind.");
        });
    }

    private static void TestRollback()
    {
        WithSavePair(8, "Original", (pair, _) =>
        {
            byte[] originalSave = File.ReadAllBytes(pair.SavePath);
            byte[] originalMeta = File.ReadAllBytes(pair.MetaPath);
            SaveDocument document = SaveDocument.Load(pair);
            Set(document, "$.coins", "777");
            SaveCommitService service = new(
                () => false,
                stage =>
                {
                    if (stage == SaveCommitStage.AfterSaveReplacement) throw new IOException("Simulated metadata replacement failure.");
                });
            SaveCommitResult result = service.Commit(document, warningsConfirmed: true);
            True(!result.Success, "Simulated failure unexpectedly succeeded.");
            True(originalSave.AsSpan().SequenceEqual(File.ReadAllBytes(pair.SavePath)), "The original .save was not restored.");
            True(originalMeta.AsSpan().SequenceEqual(File.ReadAllBytes(pair.MetaPath)), "The original .meta was not restored.");
        });
    }

    private static void TestGameRunningBlock()
    {
        WithSavePair(8, "Original", (pair, _) =>
        {
            SaveDocument document = SaveDocument.Load(pair);
            Set(document, "$.coins", "10");
            SaveCommitResult result = new SaveCommitService(() => true).Commit(document, warningsConfirmed: true);
            True(!result.Success, "Commit was allowed while the game was running.");
            True(result.Issues.Any(issue => issue.Message.Contains("Close Data Center", StringComparison.Ordinal)), "Game-running error was missing.");
        });
    }

    private static void TestWarningConfirmation()
    {
        WithSavePair(8, "Original", (pair, _) =>
        {
            SaveDocument document = SaveDocument.Load(pair);
            ScalarEditResult edit = document.TrySetScalar(RequiredNode(document.Binary, "$.coins").NodeId, "-1");
            True(edit.Success && edit.Issues.Any(issue => issue.Severity == ValidationSeverity.Warning), "Negative coin warning was missing.");
            SaveCommitResult result = new SaveCommitService(() => false).Commit(document);
            True(!result.Success && result.Message == "Warnings require confirmation.", "Commit did not require warning confirmation.");
        });
    }

    private static void TestRealSave(string path)
    {
        byte[] input = File.ReadAllBytes(path);
        NrbfDocument document = NrbfDocument.Parse(input);
        Equal("SaveData", document.Root.SerializedType);
        True(input.AsSpan().SequenceEqual(document.ToArray()), "Real save no-op output was not byte-identical.");
        True(document.ScalarNodes.Any(node => node.Path == "$.coins"), "Real save did not expose coins.");

        SavePair pair = SavePair.FromSavePath(path);
        if (File.Exists(pair.MetaPath))
        {
            SaveDocument saveDocument = SaveDocument.Load(pair);
            True(saveDocument.CanWrite, string.Join(" ", saveDocument.Validate().Select(issue => issue.Message)));
            True(input.AsSpan().SequenceEqual(saveDocument.CreateSaveBytes()), "SaveDocument changed the real save without edits.");
        }
    }

    private static void WithSavePair(int version, string name, Action<SavePair, string> action, string? binaryName = "Original")
    {
        string directory = NewTemporaryDirectory();
        try
        {
            SavePair pair = new("test", Path.Combine(directory, "test.save"), Path.Combine(directory, "test.meta"));
            byte[] save = SyntheticNrbf.CreateScalarSave(binaryName);
            if (version != 8)
            {
                NrbfDocument document = NrbfDocument.Parse(save);
                Set(document, "$.version", version.ToString());
                save = document.ToArray();
            }
            File.WriteAllBytes(pair.SavePath, save);
            File.WriteAllText(pair.MetaPath, Metadata(version, name), new UTF8Encoding(false));
            action(pair, directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string NewTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "DataCenterSaveEditor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Metadata(int version, string name) => $"{{\"version\":{version},\"nameOfSave\":\"{name}\"}}";

    private static SaveNode RequiredNode(NrbfDocument document, string path) =>
        document.FindByPath(path) ?? throw new Exception($"Node '{path}' was not found.");

    private static void Set(NrbfDocument document, string path, string value)
    {
        ScalarEditResult result = document.TrySetScalar(RequiredNode(document, path).NodeId, value);
        True(result.Success, string.Join(" ", result.Issues.Select(issue => issue.Message)));
    }

    private static void Set(SaveDocument document, string path, string value)
    {
        ScalarEditResult result = document.TrySetScalar(RequiredNode(document.Binary, path).NodeId, value);
        True(result.Success, string.Join(" ", result.Issues.Select(issue => issue.Message)));
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected '{expected}', got '{actual}'.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new Exception($"Expected {typeof(TException).Name}.");
    }
}

internal static class SyntheticNrbf
{
    public static byte[] CreateScalarSave(string? saveName = "Original")
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        Header(writer, rootId: 1);
        Library(writer);

        WriteTypedClassHeader(
            writer,
            objectId: 1,
            "SaveData",
            ["version", "nameOfSave", "coins", "reputation", "isUnlocked", "mode", "position"],
            [0, 1, 0, 0, 0, 4, 4],
            primitiveTypes: [8, null, 11, 11, 1, null, null],
            classTypes: [null, null, null, null, null, "GameMode", "UnityEngine.Vector3"]);

        writer.Write(8);
        if (saveName is null)
        {
            writer.Write((byte)10);
        }
        else
        {
            writer.Write((byte)6);
            writer.Write(2);
            writer.Write(saveName);
        }
        writer.Write(12.5f);
        writer.Write(93f);
        writer.Write(true);

        WriteTypedClassHeader(writer, 3, "GameMode", ["value__"], [0], [8], [null]);
        writer.Write(2);
        WriteTypedClassHeader(writer, 4, "UnityEngine.Vector3", ["x", "y", "z"], [0, 0, 0], [11, 11, 11], [null, null, null]);
        writer.Write(1f);
        writer.Write(2f);
        writer.Write(3f);
        writer.Write((byte)11);
        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] CreateArraySave()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        Header(writer, rootId: 1);
        Library(writer);
        WriteTypedClassHeader(writer, 1, "ArrayRoot", ["numbers", "names"], [2, 2], [null, null], [null, null]);

        writer.Write((byte)15);
        writer.Write(2);
        writer.Write(3);
        writer.Write((byte)8);
        writer.Write(10);
        writer.Write(20);
        writer.Write(30);

        writer.Write((byte)17);
        writer.Write(3);
        writer.Write(3);
        writer.Write((byte)6);
        writer.Write(4);
        writer.Write("shared");
        writer.Write((byte)10);
        writer.Write((byte)9);
        writer.Write(4);
        writer.Write((byte)11);
        writer.Flush();
        return stream.ToArray();
    }

    private static void Header(BinaryWriter writer, int rootId)
    {
        writer.Write((byte)0);
        writer.Write(rootId);
        writer.Write(-1);
        writer.Write(1);
        writer.Write(0);
    }

    private static void Library(BinaryWriter writer)
    {
        writer.Write((byte)12);
        writer.Write(2);
        writer.Write("Assembly-CSharp");
    }

    private static void WriteTypedClassHeader(
        BinaryWriter writer,
        int objectId,
        string className,
        string[] memberNames,
        byte[] binaryTypes,
        byte?[] primitiveTypes,
        string?[] classTypes)
    {
        writer.Write((byte)5);
        writer.Write(objectId);
        writer.Write(className);
        writer.Write(memberNames.Length);
        foreach (string memberName in memberNames) writer.Write(memberName);
        foreach (byte binaryType in binaryTypes) writer.Write(binaryType);
        for (int i = 0; i < binaryTypes.Length; i++)
        {
            if (binaryTypes[i] == 0) writer.Write(primitiveTypes[i] ?? throw new InvalidOperationException());
            if (binaryTypes[i] == 4)
            {
                writer.Write(classTypes[i] ?? throw new InvalidOperationException());
                writer.Write(2);
            }
        }
        writer.Write(2);
    }
}
