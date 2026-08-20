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

    /// <summary>
    /// Whether this tab exists for whoever is looking at all.
    ///
    /// Separate from <see cref="UsefulToReaders"/>, which is about a role inside the static. This is
    /// about the person: the debug tab is a window onto raw addon values and is noise on anybody
    /// else's screen. Default true, because every tab that is a feature is one for everybody.
    /// </summary>
    bool Available => true;

    void Draw();
}
