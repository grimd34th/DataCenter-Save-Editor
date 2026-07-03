using System.Diagnostics;

namespace DataCenterSaveEditor.Core;

public enum SaveCommitStage
{
    BeforeSaveReplacement,
    AfterSaveReplacement,
    BeforeMetadataReplacement,
    AfterMetadataReplacement
}

public sealed record SaveCommitResult(
    bool Success,
    string? BackupDirectory,
    IReadOnlyList<ValidationIssue> Issues,
    string? Message = null);

public sealed class SaveCommitService
{
    private readonly Func<bool> _isGameRunning;
    private readonly Action<SaveCommitStage>? _stageHook;

    public SaveCommitService(Func<bool>? isGameRunning = null, Action<SaveCommitStage>? stageHook = null)
    {
        _isGameRunning = isGameRunning ?? IsDataCenterRunning;
        _stageHook = stageHook;
    }

    public SaveCommitResult Commit(SaveDocument document, bool warningsConfirmed = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        SavePair pair = document.Pair;
        List<ValidationIssue> issues = [.. document.Validate()];

        if (!File.Exists(pair.SavePath) || !File.Exists(pair.MetaPath))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Both the original .save and .meta files must exist."));
        }

        if (_isGameRunning()) issues.Add(new ValidationIssue(ValidationSeverity.Error, "Close Data Center before saving changes."));
        if (issues.Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            return new SaveCommitResult(false, null, issues, "Preflight validation failed.");
        }

        if (!warningsConfirmed && issues.Any(issue => issue.Severity == ValidationSeverity.Warning))
        {
            return new SaveCommitResult(false, null, issues, "Warnings require confirmation.");
        }

        if (!document.IsChanged) return new SaveCommitResult(true, null, issues, "There are no pending changes.");

        string directory = Path.GetDirectoryName(pair.SavePath)
            ?? throw new InvalidOperationException("The save path has no parent directory.");
        string token = Guid.NewGuid().ToString("N");
        string temporarySave = Path.Combine(directory, $".{pair.BaseName}.{token}.save.tmp");
        string temporaryMeta = Path.Combine(directory, $".{pair.BaseName}.{token}.meta.tmp");
        string? backupDirectory = null;

        try
        {
            WriteTemporaryFile(temporarySave, document.CreateSaveBytes());
            WriteTemporaryFile(temporaryMeta, document.CreateMetadataBytes());
            SaveDocument generated = SaveDocument.Load(
                new SavePair(pair.BaseName, temporarySave, temporaryMeta),
                File.ReadAllBytes(temporarySave),
                File.ReadAllBytes(temporaryMeta));
            ValidationIssue[] generatedErrors = generated.Validate()
                .Where(issue => issue.Severity == ValidationSeverity.Error)
                .ToArray();
            if (generatedErrors.Length > 0)
            {
                return new SaveCommitResult(false, null, generatedErrors, "Generated files failed validation.");
            }

            backupDirectory = CreateBackup(pair);
            _stageHook?.Invoke(SaveCommitStage.BeforeSaveReplacement);
            File.Move(temporarySave, pair.SavePath, overwrite: true);
            _stageHook?.Invoke(SaveCommitStage.AfterSaveReplacement);
            _stageHook?.Invoke(SaveCommitStage.BeforeMetadataReplacement);
            File.Move(temporaryMeta, pair.MetaPath, overwrite: true);
            _stageHook?.Invoke(SaveCommitStage.AfterMetadataReplacement);
            return new SaveCommitResult(true, backupDirectory, issues, "Changes were saved successfully.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            List<ValidationIssue> failures = [.. issues, new ValidationIssue(ValidationSeverity.Error, $"Save failed: {ex.Message}")];
            if (backupDirectory is not null)
            {
                try
                {
                    RestoreBackup(pair, backupDirectory);
                }
                catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
                {
                    failures.Add(new ValidationIssue(ValidationSeverity.Error, $"Rollback also failed: {rollbackException.Message}"));
                }
            }

            return new SaveCommitResult(false, backupDirectory, failures, "The original files were restored when possible.");
        }
        finally
        {
            TryDelete(temporarySave);
            TryDelete(temporaryMeta);
        }
    }

    public static bool IsDataCenterRunning()
    {
        try
        {
            Process[] processes = Process.GetProcesses();
            try
            {
                return processes.Any(process => string.Equals(process.ProcessName, "Data Center", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                foreach (Process process in processes) process.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }

    private static void WriteTemporaryFile(string path, byte[] bytes)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string CreateBackup(SavePair pair)
    {
        string parent = Path.GetDirectoryName(pair.SavePath)!;
        string safeName = string.Concat(pair.BaseName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        string root = Path.Combine(parent, "SaveEditorBackups", safeName);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        string backup = Path.Combine(root, stamp);
        for (int suffix = 1; Directory.Exists(backup); suffix++) backup = Path.Combine(root, $"{stamp}-{suffix}");

        Directory.CreateDirectory(backup);
        File.Copy(pair.SavePath, Path.Combine(backup, Path.GetFileName(pair.SavePath)), overwrite: false);
        File.Copy(pair.MetaPath, Path.Combine(backup, Path.GetFileName(pair.MetaPath)), overwrite: false);
        return backup;
    }

    private static void RestoreBackup(SavePair pair, string backupDirectory)
    {
        File.Copy(Path.Combine(backupDirectory, Path.GetFileName(pair.SavePath)), pair.SavePath, overwrite: true);
        File.Copy(Path.Combine(backupDirectory, Path.GetFileName(pair.MetaPath)), pair.MetaPath, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Temporary-file cleanup must not hide the commit or rollback result.
        }
    }
}
