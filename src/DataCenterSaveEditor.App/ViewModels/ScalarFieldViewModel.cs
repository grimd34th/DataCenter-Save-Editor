using DataCenterSaveEditor.App.Infrastructure;
using DataCenterSaveEditor.Core;

namespace DataCenterSaveEditor.App.ViewModels;

internal sealed class ScalarFieldViewModel : ObservableObject
{
    private readonly SaveDocument _document;
    private readonly SaveNode _node;
    private readonly Action _changed;
    private string _value;
    private string _validationMessage = string.Empty;

    public ScalarFieldViewModel(SaveDocument document, SaveNode node, string displayName, Action changed)
    {
        _document = document;
        _node = node;
        _changed = changed;
        DisplayName = displayName;
        _value = node.Value ?? string.Empty;
    }

    public string NodeId => _node.NodeId;
    public string DisplayName { get; }
    public string Path => _node.Path;
    public string Type => _node.ScalarType?.ToString() ?? _node.SerializedType;
    public bool IsReadOnly => _node.Name == "version" && ReferenceEquals(_node, _document.Binary.Root.Children.FirstOrDefault(child => child.Name == "version"));
    public bool HasError => !string.IsNullOrWhiteSpace(_validationMessage);

    public string Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value)) return;
            ScalarEditResult result = _document.TrySetScalar(_node.NodeId, value);
            ValidationMessage = string.Join(" ", result.Issues.Select(issue => issue.Message));
            _changed();
        }
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public void Refresh()
    {
        _value = _node.Value ?? string.Empty;
        _validationMessage = string.Empty;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(HasError));
    }
}
