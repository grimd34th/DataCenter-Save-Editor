using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using DataCenterSaveEditor.App.Infrastructure;
using DataCenterSaveEditor.Core;
using Microsoft.Win32;

namespace DataCenterSaveEditor.App.ViewModels;

internal sealed class MainViewModel : ObservableObject
{
    private static readonly string[] PositionTerms = ["position", "rotation", "quaternion", "scale"];
    private readonly SaveCommitService _commitService = new();
    private readonly Dictionary<string, ScalarFieldViewModel> _commonByNodeId = new(StringComparer.Ordinal);
    private SavePair? _selectedPair;
    private SaveDocument? _document;
    private string _searchText = string.Empty;
    private NodeViewModel? _selectedAdvancedNode;
    private string _advancedValue = string.Empty;
    private string _advancedValidation = string.Empty;
    private string _status = "Select a save pair, then choose Load.";

    public MainViewModel()
    {
        RefreshCommand = new RelayCommand(Refresh);
        OpenCommand = new RelayCommand(Open);
        LoadCommand = new RelayCommand(LoadSelected, () => SelectedPair is not null);
        ApplyAdvancedCommand = new RelayCommand(ApplyAdvanced, () => CanEditAdvanced);
        RevertCommand = new RelayCommand(Revert, () => Document?.IsChanged == true);
        SaveCommand = new RelayCommand(Save, () => Document?.IsChanged == true);
    }

    public ObservableCollection<SavePair> SavePairs { get; } = [];
    public ObservableCollection<ScalarFieldViewModel> CommonFields { get; } = [];
    public ObservableCollection<NodeViewModel> AdvancedRoots { get; } = [];
    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand LoadCommand { get; }
    public RelayCommand ApplyAdvancedCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand SaveCommand { get; }

    public SavePair? SelectedPair
    {
        get => _selectedPair;
        set
        {
            if (SetProperty(ref _selectedPair, value)) LoadCommand.RaiseCanExecuteChanged();
        }
    }

    public SaveDocument? Document
    {
        get => _document;
        private set
        {
            if (SetProperty(ref _document, value)) OnPropertyChanged(nameof(DocumentTitle));
        }
    }

    public string DocumentTitle => Document is null
        ? "No save loaded"
        : $"{Document.Pair.BaseName}  •  version {Document.MetadataVersion}";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) RebuildAdvancedTree();
        }
    }

    public NodeViewModel? SelectedAdvancedNode
    {
        get => _selectedAdvancedNode;
        set
        {
            if (!SetProperty(ref _selectedAdvancedNode, value)) return;
            AdvancedValue = value?.Node.Value ?? string.Empty;
            AdvancedValidation = string.Empty;
            OnPropertyChanged(nameof(SelectedPath));
            OnPropertyChanged(nameof(SelectedType));
            OnPropertyChanged(nameof(CanEditAdvanced));
            ApplyAdvancedCommand.RaiseCanExecuteChanged();
        }
    }

    public string SelectedPath => SelectedAdvancedNode?.Node.Path ?? string.Empty;
    public string SelectedType => SelectedAdvancedNode?.Node.SerializedType ?? string.Empty;
    public bool CanEditAdvanced => Document is not null && SelectedAdvancedNode?.IsEditable == true;

    public string AdvancedValue
    {
        get => _advancedValue;
        set => SetProperty(ref _advancedValue, value);
    }

    public string AdvancedValidation
    {
        get => _advancedValidation;
        private set => SetProperty(ref _advancedValidation, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public void Refresh()
    {
        string? previous = SelectedPair?.SavePath;
        SavePairs.Clear();
        foreach (SavePair pair in SavePairLocator.Discover()) SavePairs.Add(pair);
        SelectedPair = SavePairs.FirstOrDefault(pair => string.Equals(pair.SavePath, previous, StringComparison.OrdinalIgnoreCase))
            ?? SavePairs.FirstOrDefault();
        Status = SavePairs.Count == 0
            ? $"No paired saves found in {SavePairLocator.DefaultSaveDirectory}"
            : $"Found {SavePairs.Count} paired save{(SavePairs.Count == 1 ? string.Empty : "s")}.";
    }

    private void Open()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Open Data Center save",
            Filter = "Data Center saves (*.save)|*.save|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(SavePairLocator.DefaultSaveDirectory) ? SavePairLocator.DefaultSaveDirectory : null
        };
        if (dialog.ShowDialog() != true) return;

        SavePair pair = SavePair.FromSavePath(dialog.FileName);
        if (!File.Exists(pair.MetaPath))
        {
            MessageBox.Show("The matching .meta file was not found.", "Cannot open save", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SavePair? existing = SavePairs.FirstOrDefault(item => string.Equals(item.SavePath, pair.SavePath, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            SavePairs.Insert(0, pair);
            existing = pair;
        }

        SelectedPair = existing;
        LoadSelected();
    }

    private void LoadSelected()
    {
        if (SelectedPair is null) return;
        if (Document?.IsChanged == true && MessageBox.Show(
                "Discard the current pending changes and load another save?",
                "Unsaved changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            LoadDocument(SaveDocument.Load(SelectedPair));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(ex.Message, "Could not load save", MessageBoxButton.OK, MessageBoxImage.Error);
            Status = "Load failed.";
        }
    }

    private void LoadDocument(SaveDocument document)
    {
        Document = document;
        BuildCommonFields();
        RebuildAdvancedTree();
        ValidationIssue[] errors = document.Validate().Where(issue => issue.Severity == ValidationSeverity.Error).ToArray();
        Status = errors.Length == 0
            ? $"Loaded {document.Binary.AllNodes.Count():N0} nodes and {document.Binary.ScalarNodes.Count():N0} editable scalar leaves."
            : $"Loaded for inspection only: {string.Join(" ", errors.Select(issue => issue.Message))}";
        RaiseEditState();
    }

    private void BuildCommonFields()
    {
        CommonFields.Clear();
        _commonByNodeId.Clear();
        if (Document is null) return;

        List<(SaveNode Node, string Label, int Rank)> fields = [];
        foreach (SaveNode node in Document.Binary.Root.Children.Where(node => node.Kind == SaveNodeKind.Scalar))
        {
            fields.Add((node, FriendlyName(node.Name), CommonRank(node.Name)));
        }

        foreach (SaveNode member in Document.Binary.Root.Children.Where(node =>
                     PositionTerms.Any(term => node.Name.Contains(term, StringComparison.OrdinalIgnoreCase))))
        {
            SaveNode? target = member.Kind == SaveNodeKind.Reference && member.ReferencedObjectId is int id
                ? Document.Binary.FindObjectById(id)
                : member;
            if (target is null) continue;
            foreach (SaveNode component in target.Children.Where(node => node.Kind == SaveNodeKind.Scalar))
            {
                fields.Add((component, $"{FriendlyName(member.Name)} {component.Name.ToUpperInvariant()}", 50));
            }
        }

        foreach ((SaveNode node, string label, _) in fields
                     .OrderBy(field => field.Rank)
                     .ThenBy(field => field.Label, StringComparer.OrdinalIgnoreCase)
                     .DistinctBy(field => field.Node.NodeId))
        {
            ScalarFieldViewModel viewModel = new(Document, node, label, OnEdited);
            CommonFields.Add(viewModel);
            _commonByNodeId[node.NodeId] = viewModel;
        }
    }

    private void RebuildAdvancedTree()
    {
        string? selectedId = SelectedAdvancedNode?.Node.NodeId;
        AdvancedRoots.Clear();
        if (Document is null) return;

        foreach (SaveNode node in Document.Binary.TopLevelNodes)
        {
            NodeViewModel? view = NodeViewModel.CreateFiltered(node, SearchText.Trim());
            if (view is not null) AdvancedRoots.Add(view);
        }

        SelectedAdvancedNode = selectedId is null ? null : FindNode(AdvancedRoots, selectedId);
    }

    private void ApplyAdvanced()
    {
        if (Document is null || SelectedAdvancedNode is null) return;
        string nodeId = SelectedAdvancedNode.Node.NodeId;
        ScalarEditResult result = Document.TrySetScalar(nodeId, AdvancedValue);
        AdvancedValidation = string.Join(" ", result.Issues.Select(issue => issue.Message));
        if (!result.Success) return;

        if (_commonByNodeId.TryGetValue(nodeId, out ScalarFieldViewModel? common)) common.Refresh();
        OnEdited();
        RebuildAdvancedTree();
    }

    private void Revert()
    {
        if (Document is null) return;
        Document.RevertAll();
        foreach (ScalarFieldViewModel field in CommonFields) field.Refresh();
        AdvancedValidation = string.Empty;
        RebuildAdvancedTree();
        Status = "Pending changes reverted.";
        RaiseEditState();
    }

    private void Save()
    {
        if (Document is null) return;
        ScalarFieldViewModel[] invalidFields = CommonFields.Where(field => field.HasError).ToArray();
        if (invalidFields.Length > 0)
        {
            MessageBox.Show("Correct invalid values in the Common tab before saving.", "Invalid values", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        IReadOnlyList<SaveChange> changes = Document.GetChanges();
        if (changes.Count == 0) return;
        StringBuilder diff = new();
        foreach (SaveChange change in changes.Take(80))
        {
            diff.AppendLine(change.Path);
            diff.AppendLine($"  {change.OldValue}  →  {change.NewValue}");
        }
        if (changes.Count > 80) diff.AppendLine($"…and {changes.Count - 80} more changes.");
        diff.AppendLine();
        diff.Append("Create a backup and write these changes?");
        if (MessageBox.Show(diff.ToString(), "Review changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        ValidationIssue[] warnings = Document.Validate().Where(issue => issue.Severity == ValidationSeverity.Warning).ToArray();
        bool warningsConfirmed = warnings.Length == 0 || MessageBox.Show(
            string.Join(Environment.NewLine, warnings.Select(issue => $"• {issue.Message}")) + Environment.NewLine + Environment.NewLine + "Continue anyway?",
            "Confirm warnings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!warningsConfirmed) return;

        SaveCommitResult result = _commitService.Commit(Document, warningsConfirmed: true);
        if (!result.Success)
        {
            MessageBox.Show(string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)), "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Status = result.Message ?? "Save failed.";
            return;
        }

        string backupMessage = result.BackupDirectory is null ? string.Empty : $"\n\nBackup: {result.BackupDirectory}";
        MessageBox.Show((result.Message ?? "Saved.") + backupMessage, "Save complete", MessageBoxButton.OK, MessageBoxImage.Information);
        SavePair pair = Document.Pair;
        LoadDocument(SaveDocument.Load(pair));
    }

    private void OnEdited()
    {
        if (Document is not null)
        {
            Status = Document.IsChanged ? $"{Document.GetChanges().Count} pending change(s)." : "No pending changes.";
        }
        RaiseEditState();
    }

    private void RaiseEditState()
    {
        SaveCommand.RaiseCanExecuteChanged();
        RevertCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(DocumentTitle));
    }

    private static NodeViewModel? FindNode(IEnumerable<NodeViewModel> roots, string nodeId)
    {
        foreach (NodeViewModel node in roots)
        {
            if (node.Node.NodeId == nodeId) return node;
            NodeViewModel? child = FindNode(node.Children, nodeId);
            if (child is not null) return child;
        }
        return null;
    }

    private static int CommonRank(string name) => name switch
    {
        "nameOfSave" => 0,
        "version" => 1,
        "coins" => 2,
        "reputation" => 3,
        _ when name.Contains("objective", StringComparison.OrdinalIgnoreCase) => 10,
        _ when name.Contains("unlock", StringComparison.OrdinalIgnoreCase) => 20,
        _ when name.Contains("command", StringComparison.OrdinalIgnoreCase) => 30,
        _ => 40
    };

    private static string FriendlyName(string value)
    {
        string spaced = Regex.Replace(value.TrimStart('_'), "(?<=[a-z0-9])([A-Z])", " $1");
        return spaced.Length == 0 ? value : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }
}
