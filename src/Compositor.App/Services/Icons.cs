using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Compositor.App.Services;

/// <summary>Line icons on a 24×24 grid, drawn as strokes (the Mac version used SF Symbols).</summary>
public static class Icons
{
    public static readonly Dictionary<string, string> Paths = new()
    {
        ["move"] = "M12,2.5 L12,21.5 M2.5,12 L21.5,12 M12,2.5 L9.3,5.2 M12,2.5 L14.7,5.2 M12,21.5 L9.3,18.8 M12,21.5 L14.7,18.8 M2.5,12 L5.2,9.3 M2.5,12 L5.2,14.7 M21.5,12 L18.8,9.3 M21.5,12 L18.8,14.7",
        ["marquee"] = "M4,5 H20 V19 H4 Z",
        ["marqueeEllipse"] = "M12,5 C16.97,5 21,8.13 21,12 C21,15.87 16.97,19 12,19 C7.03,19 3,15.87 3,12 C3,8.13 7.03,5 12,5 Z",
        ["lasso"] = "M7.5,15.5 C3.5,13.5 3.8,6.5 11.5,5.5 C18.5,4.6 21.5,8.5 19.5,12 C17.8,15 12,16.5 7.5,15.5 Z M7.5,15.5 C6.5,17.5 7.5,19.5 9.5,19.3 M9.5,19.3 C10.8,19.8 10.6,21.3 9.6,21.6",
        ["polygonalLasso"] = "M5,16 L7.5,5 L19.5,7.5 L17,16 Z M5,16 L6.5,20.5",
        ["wand"] = "M3.5,20.5 L14,10 M15,3 V6.5 M13.25,4.75 H16.75 M19.5,7.5 V10 M18.25,8.75 H20.75 M18,13.5 V16 M16.75,14.75 H19.25",
        ["crop"] = "M7,2.5 V17 H21.5 M2.5,7 H17 V21.5",
        ["brush"] = "M20,3.5 L10.5,13 M10.5,13 C8.5,11.8 5.2,12.6 5,15.6 C4.9,17.6 4,18.9 3,19.8 C6.8,20.6 11,19.6 11.6,16 C11.8,14.8 11.4,13.6 10.5,13 Z",
        ["eraser"] = "M14.5,4 L20.5,10 L11.5,19 H6.5 L3.5,16 Z M9.5,9 L15.5,15 M11.5,19 H20.5",
        ["remove"] = "M12,8 L17,13 L10,20 H6 L3,17 Z M7.5,12.5 L12.5,17.5 M18,2.5 V7.5 M15.5,5 H20.5 M20.5,10 V13 M19,11.5 H22",
        ["heal"] = "M8.2,15.8 L15.8,8.2 C17.6,6.4 20.1,6.4 21,7.3 C21.9,8.2 21.9,10.7 20.1,12.5 L12.5,20.1 C10.7,21.9 8.2,21.9 7.3,21 C6.4,20.1 6.4,17.6 8.2,15.8 Z M3,3 L5,5 M3,8 H5 M8,3 V5 M12.5,12 L12.5,12.1 M14.5,14 L14.5,14.1 M12.5,14.5 L12.5,14.6",
        ["stamp"] = "M9.5,10.5 C7.5,9 7.5,4 12,4 C16.5,4 16.5,9 14.5,10.5 V13 H19 C20,13 20.5,13.5 20.5,14.5 V16.5 H3.5 V14.5 C3.5,13.5 4,13 5,13 H9.5 Z M5,19.5 H19",
        ["smear"] = "M12,3 C12,3 6,10 6,14.5 C6,17.8 8.7,20.5 12,20.5 C15.3,20.5 18,17.8 18,14.5 C18,10 12,3 12,3 Z",
        ["gradient"] = "M4,4.5 H20 V19.5 H4 Z M7,4.5 V19.5 M10,4.5 V19.5 M10,8 H12.5 M10,12 H12.5 M10,16 H12.5 M14.5,6 V7 M14.5,10 V11 M14.5,14 V15 M14.5,18 V19 M17.5,8.5 V9 M17.5,15 V15.5",
        ["shapeRect"] = "M5,5.5 H19 V18.5 H5 Z",
        ["shapeEllipse"] = "M12,5 C16.4,5 20,8.1 20,12 C20,15.9 16.4,19 12,19 C7.6,19 4,15.9 4,12 C4,8.1 7.6,5 12,5 Z",
        ["shapeLine"] = "M5,19 L19,5",
        ["type"] = "M5.5,6.5 V4.5 H18.5 V6.5 M12,4.5 V19.5 M9.5,19.5 H14.5",
        ["eyedropper"] = "M15,5.5 L18.5,9 M16.5,7.5 L19,5 C19.8,4.2 21,5.4 20.2,6.2 L17.7,8.7 M16,8 L7,17 L5.5,17.4 L4.5,19.5 L6.6,18.5 L7,17",
        ["hand"] = "M8,13 V6.5 C8,5.4 9.5,5.4 9.5,6.5 V11 M9.5,11 V4.8 C9.5,3.7 11,3.7 11,4.8 V11 M11,11 V5.3 C11,4.2 12.5,4.2 12.5,5.3 V11 M12.5,11 V7.3 C12.5,6.2 14,6.2 14,7.3 V14.5 C14,18.2 12.2,20.5 9.5,20.5 C7.3,20.5 6.1,19.2 5,17 L3.8,14.5 C3.3,13.4 4.6,12.7 5.4,13.4 L8,15.8",
        ["zoom"] = "M10.5,4 C14.09,4 17,6.91 17,10.5 C17,14.09 14.09,17 10.5,17 C6.91,17 4,14.09 4,10.5 C4,6.91 6.91,4 10.5,4 Z M15.2,15.2 L20.5,20.5",
        ["zoomIn"] = "M10.5,4 C14.09,4 17,6.91 17,10.5 C17,14.09 14.09,17 10.5,17 C6.91,17 4,14.09 4,10.5 C4,6.91 6.91,4 10.5,4 Z M15.2,15.2 L20.5,20.5 M10.5,7.5 V13.5 M7.5,10.5 H13.5",
        ["zoomOut"] = "M10.5,4 C14.09,4 17,6.91 17,10.5 C17,14.09 14.09,17 10.5,17 C6.91,17 4,14.09 4,10.5 C4,6.91 6.91,4 10.5,4 Z M15.2,15.2 L20.5,20.5 M7.5,10.5 H13.5",
        ["eye"] = "M2.5,12 C5.3,7.5 8.6,5.8 12,5.8 C15.4,5.8 18.7,7.5 21.5,12 C18.7,16.5 15.4,18.2 12,18.2 C8.6,18.2 5.3,16.5 2.5,12 Z M12,9 C13.66,9 15,10.34 15,12 C15,13.66 13.66,15 12,15 C10.34,15 9,13.66 9,12 C9,10.34 10.34,9 12,9 Z",
        ["folder"] = "M3,6.5 C3,5.7 3.7,5 4.5,5 H9.2 L11.2,7.2 H19.5 C20.3,7.2 21,7.9 21,8.7 V17.5 C21,18.3 20.3,19 19.5,19 H4.5 C3.7,19 3,18.3 3,17.5 Z",
        ["newFolder"] = "M3,6.5 C3,5.7 3.7,5 4.5,5 H9.2 L11.2,7.2 H19.5 C20.3,7.2 21,7.9 21,8.7 V17.5 C21,18.3 20.3,19 19.5,19 H4.5 C3.7,19 3,18.3 3,17.5 Z M12,10 V16 M9,13 H15",
        ["newLayer"] = "M5.5,3.5 H14.5 L18.5,7.5 V20.5 H5.5 Z M12,9.5 V16.5 M8.5,13 H15.5",
        ["mask"] = "M3.5,5 H20.5 V19 H3.5 Z M12,8.2 C14.1,8.2 15.8,9.9 15.8,12 C15.8,14.1 14.1,15.8 12,15.8 C9.9,15.8 8.2,14.1 8.2,12 C8.2,9.9 9.9,8.2 12,8.2 Z",
        ["trash"] = "M4,7 H20 M9,7 V4.5 H15 V7 M6,7 L7,20 H17 L18,7 M10,11 V16 M14,11 V16",
        ["link"] = "M9,15 L15,9 M7.8,12.2 L5.9,14.1 C4.5,15.5 4.5,17.9 5.9,19.3 C7.3,20.7 9.7,20.7 11.1,19.3 L13,17.4 M11,6.6 L12.9,4.7 C14.3,3.3 16.7,3.3 18.1,4.7 C19.5,6.1 19.5,8.5 18.1,9.9 L16.2,11.8",
        ["chevronRight"] = "M9,6 L15,12 L9,18",
        ["chevronDown"] = "M6,9 L12,15 L18,9",
        ["plus"] = "M12,5 V19 M5,12 H19",
        ["clip"] = "M8,5 V13 C8,15 9,16 11,16 H18 M15,13 L18,16 L15,19",
        ["swap"] = "M7,4 H15 C17,4 18,5 18,7 V14 M15,11 L18,14 L21,11",
        ["close"] = "M7,7 L17,17 M17,7 L7,17",
        ["open"] = "M3,7 C3,6 3.8,5 5,5 H9.5 L11.5,7 H18 C19,7 20,7.8 20,9 V10 M3,7 V18 L6,11 H22 L19,18 H3",
        ["image"] = "M3.5,5 H20.5 V19 H3.5 Z M3.5,16 L9,11 L13,15 L16,12 L20.5,16 M15.5,8.5 C16.3,8.5 17,9.2 17,10 C17,10.8 16.3,11.5 15.5,11.5",
    };

    /// <summary>An icon as a sized element, stroked in the current text color unless told otherwise.</summary>
    public static FrameworkElement Make(string key, double size = 18, Brush? stroke = null, double thickness = 1.6, bool dashed = false)
    {
        var path = new Path
        {
            Data = Geometry.Parse(Paths.TryGetValue(key, out var data) ? data : Paths["plus"]),
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            SnapsToDevicePixels = false,
        };
        if (dashed) path.StrokeDashArray = new DoubleCollection { 1.6, 1.4 };
        if (stroke is not null) path.Stroke = stroke;
        else path.SetResourceReference(Shape.StrokeProperty, "TextBrush");
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform };
    }
}
