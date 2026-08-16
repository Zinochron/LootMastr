using System.Reflection;

namespace LootMastr.Data;

/// <summary>
/// Which build this is. Written into the window title and every capture file, because a capture
/// that does not say which build produced it cannot be read: one arrived recorded by a plugin
/// older than the recorder it was asking about, and nothing in the file said so.
/// </summary>
public static class Build
{
    private static string? version;

    public static string Version =>
        version ??= Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
}
