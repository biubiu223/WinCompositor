using Compositor.Core.Model;

namespace Compositor.Core.History;

/// <summary>
/// Undo history of whole-document snapshots. Snapshots share immutable images, so a layer edit costs no pixel copies.
/// Nestable transactions group a complete gesture into one step. Mirrors DocumentHistory.swift.
/// </summary>
public sealed class DocumentHistory
{
    public sealed record Snapshot(CanvasDocument? Document, Guid? ActiveLayerId, Guid Revision);

    private sealed record Entry(string Name, Snapshot Before, Snapshot After);

    private readonly List<Entry> _past = new();
    private readonly List<Entry> _future = new();
    private Guid _revision = Guid.NewGuid();
    private Guid? _savedRevision;
    private Snapshot? _pending;
    private string _pendingName = "Edit";
    private int _depth;

    public int EntryLimit { get; }
    public long RetainedByteLimit { get; }

    public event EventHandler? Changed;

    public DocumentHistory(int entryLimit = 100, long retainedByteLimit = 1024L * 1024 * 1024)
    {
        EntryLimit = Math.Max(0, entryLimit);
        RetainedByteLimit = Math.Max(0, retainedByteLimit);
        _savedRevision = _revision;
    }

    public bool CanUndo => _depth == 0 && _past.Count > 0;
    public bool CanRedo => _depth == 0 && _future.Count > 0;
    public string UndoName => _past.Count > 0 ? _past[^1].Name : "";
    public string RedoName => _future.Count > 0 ? _future[^1].Name : "";
    public bool IsModified => _revision != _savedRevision;
    public int UndoCount => _past.Count;
    public bool InTransaction => _depth > 0;
    public Guid CurrentRevision => _revision;

    /// <summary>Names of the steps, oldest first, for a History panel.</summary>
    public IReadOnlyList<string> PastNames => _past.Select(e => e.Name).ToList();

    public void MarkSaved() { _savedRevision = _revision; Changed?.Invoke(this, EventArgs.Empty); }
    public void MarkSaved(Guid saved) { _savedRevision = saved; Changed?.Invoke(this, EventArgs.Empty); }

    public void Reset()
    {
        _past.Clear();
        _future.Clear();
        _pending = null;
        _depth = 0;
        _revision = Guid.NewGuid();
        _savedRevision = _revision;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Begin(string name, CanvasDocument? document, Guid? selection)
    {
        if (_depth == 0)
        {
            _pending = new Snapshot(document, selection, _revision);
            _pendingName = name;
        }
        _depth++;
    }

    public void End(CanvasDocument? document, Guid? selection)
    {
        if (_depth <= 0) return;
        _depth--;
        if (_depth != 0 || _pending is not { } before) return;
        _pending = null;
        // Selecting, navigating and no-op edits must preserve redo history.
        if (Equals(before.Document, document)) { Changed?.Invoke(this, EventArgs.Empty); return; }
        _revision = Guid.NewGuid();
        _past.Add(new Entry(_pendingName, before, new Snapshot(document, selection, _revision)));
        _future.Clear();
        Trim(document);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Abandons an open transaction without recording it (the document is restored by the caller).</summary>
    public Snapshot? Abort()
    {
        var pending = _pending;
        _pending = null;
        _depth = 0;
        return pending;
    }

    public Snapshot? Undo()
    {
        if (!CanUndo) return null;
        var entry = _past[^1];
        _past.RemoveAt(_past.Count - 1);
        _future.Add(entry);
        _revision = entry.Before.Revision;
        Trim(entry.Before.Document);
        Changed?.Invoke(this, EventArgs.Empty);
        return entry.Before;
    }

    public Snapshot? Redo()
    {
        if (!CanRedo) return null;
        var entry = _future[^1];
        _future.RemoveAt(_future.Count - 1);
        _past.Add(entry);
        _revision = entry.After.Revision;
        Trim(entry.After.Document);
        Changed?.Invoke(this, EventArgs.Empty);
        return entry.After;
    }

    /// <summary>Bytes retained only by history, excluding images in the live document.</summary>
    public long RetainedBytes(CanvasDocument? current)
    {
        var seen = new HashSet<RasterImage>(ReferenceEqualityComparer.Instance);
        foreach (var layer in current?.Layers ?? Enumerable.Empty<Layer>())
        {
            if (layer.Asset is { } a) seen.Add(a);
            if (layer.Mask?.Asset is { } m) seen.Add(m);
        }
        long bytes = 0;
        foreach (var entry in _past.Concat(_future))
        {
            foreach (var snapshot in new[] { entry.Before, entry.After })
            {
                foreach (var layer in snapshot.Document?.Layers ?? Enumerable.Empty<Layer>())
                {
                    foreach (var image in new[] { layer.Asset, layer.Mask?.Asset })
                    {
                        if (image is not null && seen.Add(image)) bytes += image.PixelCount * (image.IsMask ? 1 : 4);
                    }
                }
            }
        }
        return bytes;
    }

    private void Trim(CanvasDocument? current)
    {
        while (_past.Count + _future.Count > EntryLimit || RetainedBytes(current) > RetainedByteLimit)
        {
            if (_past.Count > 0) _past.RemoveAt(0);
            else if (_future.Count > 0) _future.RemoveAt(0);
            else break;
        }
    }
}
