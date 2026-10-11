using Compositor.Core.Model;
using Compositor.Core.Segmentation;

namespace Compositor.Core.Inpainting;

/// <summary>
/// The Remove tool's stroke made into the area to fill: what a loop encircles counts as painted, and (with the subject
/// model) an object the stroke mostly covers is taken whole, edge to edge, rather than as roughly as it was painted.
/// </summary>
public static class RemoveMask
{
    /// <summary>The stroke (255 on) with everything it fully encloses added, as when an object is circled.</summary>
    public static byte[] CloseLoops(byte[] stroke, int width, int height)
    {
        var bounds = FillMasks.Bounds(stroke, width, height);
        if (bounds.Width == 0) return stroke;
        // Flood the outside, from a ring just past the stroke's bounds; whatever the flood can't reach is enclosed.
        int left = Math.Max(0, bounds.X - 1), top = Math.Max(0, bounds.Y - 1);
        int right = Math.Min(width - 1, bounds.X + bounds.Width), bottom = Math.Min(height - 1, bounds.Y + bounds.Height);
        int w = right - left + 1, h = bottom - top + 1;
        var outside = new bool[w * h];
        var stack = new Stack<int>();
        void Seed(int x, int y)
        {
            int i = (y - top) * w + (x - left);
            if (outside[i] || stroke[y * width + x] != 0) return;
            outside[i] = true;
            stack.Push(i);
        }
        for (int x = left; x <= right; x++) { Seed(x, top); Seed(x, bottom); }
        for (int y = top; y <= bottom; y++) { Seed(left, y); Seed(right, y); }
        while (stack.Count > 0)
        {
            int i = stack.Pop(), x = i % w + left, y = i / w + top;
            if (x > left) Seed(x - 1, y);
            if (x < right) Seed(x + 1, y);
            if (y > top) Seed(x, y - 1);
            if (y < bottom) Seed(x, y + 1);
        }
        var result = (byte[])stroke.Clone();
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                if (!outside[(y - top) * w + (x - left)]) result[y * width + x] = 255;
        return result;
    }

    /// <summary>
    /// Objects the subject model finds around the stroke, added whole when the stroke covers at least
    /// <paramref name="coverage"/> of them (and they aren't most of what it looked at, which would be background).
    /// </summary>
    public static byte[] SnapToObjects(ISubjectModel model, byte[] rgba, byte[] stroke, int width, int height, double coverage = 0.4)
    {
        var bounds = FillMasks.Bounds(stroke, width, height);
        if (bounds.Width == 0) return stroke;
        int pad = Math.Max(48, Math.Max(bounds.Width, bounds.Height) / 2);
        int left = Math.Max(0, bounds.X - pad), top = Math.Max(0, bounds.Y - pad);
        int right = Math.Min(width, bounds.X + bounds.Width + pad), bottom = Math.Min(height, bounds.Y + bounds.Height + pad);
        int w = right - left, h = bottom - top;
        var crop = new byte[w * h * 4];
        for (int y = 0; y < h; y++) rgba.AsSpan(((top + y) * width + left) * 4, w * 4).CopyTo(crop.AsSpan(y * w * 4));
        var objects = SubjectMask.Threshold(SubjectMask.Resize(SubjectMask.Infer(model, crop, w, h), model.Spec.Side, w, h));
        var label = new int[w * h];
        var result = (byte[])stroke.Clone();
        int next = 0;
        var stack = new Stack<int>();
        var members = new List<int>();
        for (int start = 0; start < objects.Length; start++)
        {
            if (objects[start] == 0 || label[start] != 0) continue;
            next++;
            members.Clear();
            int covered = 0;
            stack.Push(start);
            label[start] = next;
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % w, y = i / w;
                members.Add(i);
                if (stroke[(top + y) * width + left + x] != 0) covered++;
                void Visit(int n)
                {
                    if (objects[n] == 0 || label[n] != 0) return;
                    label[n] = next;
                    stack.Push(n);
                }
                if (x > 0) Visit(i - 1);
                if (x + 1 < w) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y + 1 < h) Visit(i + w);
            }
            if (covered < members.Count * coverage || members.Count > w * h / 2) continue;
            foreach (int i in members) result[(top + i / w) * width + left + i % w] = 255;
        }
        return result;
    }
}
