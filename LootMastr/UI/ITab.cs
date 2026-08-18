using System;

namespace LootMastr.UI;

/// <summary>A single tab inside <see cref="MainWindow"/>.</summary>
public interface ITab : IDisposable
{
    /// <summary>Label shown on the tab itself.</summary>
    string Title { get; }

    /// <summary>Stable ImGui id, kept separate from <see cref="Title"/> so labels may change freely.</summary>
    string Id { get; }

    /// <summary>
    /// Whether this tab is worth showing to somebody who may only read.
    ///
    /// Default true, because most of the plugin is worth reading whoever you are — the roster, the
    /// plan, what the group has decided. The exception is a tab whose every control would be greyed
    /// out, which is not a read-only view of anything, just a wall of things you cannot press.
    /// </summary>
    bool UsefulToReaders => true;

    void Draw();
}
