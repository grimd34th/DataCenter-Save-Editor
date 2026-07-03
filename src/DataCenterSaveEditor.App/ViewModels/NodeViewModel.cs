using DataCenterSaveEditor.Core;

namespace DataCenterSaveEditor.App.ViewModels;

internal sealed class NodeViewModel
{
    private NodeViewModel(SaveNode node, IReadOnlyList<NodeViewModel> children)
    {
        Node = node;
        Children = children;
    }

    public SaveNode Node { get; }
    public IReadOnlyList<NodeViewModel> Children { get; }
    public string Path => Node.Path;
    public string Type => Node.SerializedType;
    public bool IsEditable => Node.Kind == SaveNodeKind.Scalar;

    public string Display => Node.Kind switch
    {
        SaveNodeKind.Scalar => $"{Node.Name}: {Node.Value}  [{Node.ScalarType}]",
        SaveNodeKind.Reference => $"{Node.Name} → object #{Node.ReferencedObjectId}  [{Node.SerializedType}]",
        SaveNodeKind.Null => $"{Node.Name}: null",
        _ => $"{Node.Name}  [{Node.SerializedType}]" + (Node.ObjectId is int id ? $"  #{id}" : string.Empty)
    };

    public static NodeViewModel? CreateFiltered(SaveNode node, string filter, bool includeAll = false)
    {
        bool ownMatch = includeAll || string.IsNullOrWhiteSpace(filter) || Matches(node, filter);
        List<NodeViewModel> children = [];
        foreach (SaveNode child in node.Children)
        {
            NodeViewModel? childView = CreateFiltered(child, filter, ownMatch);
            if (childView is not null) children.Add(childView);
        }

        return ownMatch || children.Count > 0 ? new NodeViewModel(node, children) : null;
    }

    private static bool Matches(SaveNode node, string filter) =>
        node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        node.Path.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        node.SerializedType.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (node.Value?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
}
