using Godot;

namespace DataSquared;

/// <summary>The grip at the start of a row. Dragging it moves the row's node.</summary>
public partial class DragHandle : Label
{
    private readonly long _id;
    private readonly string _caption;

    private DragHandle() : this(0, "") { }

    public DragHandle(long id, string caption)
    {
        _id = id;
        _caption = caption;
        Text = "⠿";
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.Drag;
        TooltipText = "Drag to move";
        Modulate = new Color(1, 1, 1, 0.35f);
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        SetDragPreview(new Label { Text = _caption });
        return CardRow.DragData(_id);
    }
}
