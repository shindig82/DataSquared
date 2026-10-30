using DataSquared.Core.Model;
using Godot;

namespace DataSquared;

/// <summary>
/// One row on a card: a value, a link to a child card, or the card's header. Rows take a right-click
/// for the context menu, and are where dragged rows get dropped.
/// </summary>
public partial class CardRow : HBoxContainer
{
    private const string DragPrefix = "datasquared-node:";

    private readonly GraphCanvas _canvas;

    // Godot needs a parameterless constructor to rebuild scripts after a hot reload.
    private CardRow() : this(null!, null!, false) { }

    public CardRow(GraphCanvas canvas, DocNode node, bool isHeader)
    {
        _canvas = canvas;
        Node = node;
        IsHeader = isHeader;
        MouseFilter = MouseFilterEnum.Pass;
    }

    /// <summary>The node this row shows. For a header row, the card's own container.</summary>
    public DocNode Node { get; }

    public bool IsHeader { get; }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
        {
            _canvas.OpenMenu(Node);
            AcceptEvent();
        }
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) =>
        Dragged(data) is { } dragged && Target(dragged, atPosition) is { } to && _canvas.CanMove(dragged, to.Parent, to.Index);

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (Dragged(data) is { } dragged && Target(dragged, atPosition) is { } to)
            _canvas.Move(dragged, to.Parent, to.Index);
    }

    public static Variant DragData(long id) => Variant.From($"{DragPrefix}{id}");

    private DocNode? Dragged(Variant data)
    {
        var text = data.VariantType == Variant.Type.String ? data.AsString() : "";
        return text.StartsWith(DragPrefix) && long.TryParse(text[DragPrefix.Length..], out var id) ? _canvas.NodeById(id) : null;
    }

    /// <summary>
    /// Where a drop lands: onto a header, at the end of that card; onto a row, before or after it
    /// depending on which half the pointer is over. The index counts as if the dragged node were taken out.
    /// </summary>
    private (DocNode Parent, int Index)? Target(DocNode dragged, Vector2 at)
    {
        if (IsHeader)
            return (Node, Node.Children.Count);
        if (Node.Parent is not { } parent || Node == dragged)
            return null;

        var index = Node.IndexInParent + (at.Y > Size.Y / 2 ? 1 : 0);
        if (dragged.Parent == parent && dragged.IndexInParent < index)
            index--;
        return (parent, index);
    }
}
