using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace BlueBubbles.Windows.Controls;

public class DraggableDivider : Grid
{
    private static readonly InputCursor ResizeCursor =
        InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private static readonly InputCursor ArrowCursor =
        InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    public DraggableDivider()
    {
        PointerEntered += (_, _) => ProtectedCursor = ResizeCursor;
        PointerExited += (_, _) => ProtectedCursor = ArrowCursor;
    }
}
