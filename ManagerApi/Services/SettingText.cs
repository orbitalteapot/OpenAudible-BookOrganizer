namespace ManagerApi.Services;

/// <summary>
/// How a path or other text setting is read, wherever it comes from (the environment, a saved
/// file or the page), so every place agrees on when two of them are the same.
/// </summary>
public static class SettingText
{
    /// <summary>A blank value means "not set"; otherwise the spaces around it are not part of it.</summary>
    public static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Whether two values are the same setting once <see cref="Normalize"/> has read them.</summary>
    public static bool Same(string? a, string? b) => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
}
