using System;

namespace LootMastr.UI;

/// <summary>A single tab inside <see cref="MainWindow"/>.</summary>
public interface ITab : IDisposable
{
    /// <summary>Label shown on the tab itself.</summary>
    string Title { get; }

    /// <summary>Stable ImGui id, kept separate from <see cref="Title"/> so labels may change freely.</summary>
    string Id { get; }

    void Draw();
}
