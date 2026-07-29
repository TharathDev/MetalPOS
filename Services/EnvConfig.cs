using System;
using System.Collections.Generic;
using System.IO;

namespace PosApp.Services;

/// <summary>
/// Lightweight configuration lookup that merges real process environment
/// variables with values parsed from a local ".env" file. Process environment
/// variables always win. The ".env" file is located by walking up from the
/// executable directory (so it works with both `dotnet run` and a published
/// build where the file sits next to the executable) and, as a fallback, the
/// current working directory.
///
/// The ".env" file is git-ignored and may contain secrets, so its values are
/// only ever read here and never logged.
/// </summary>
internal static class EnvConfig
{
    private static readonly Dictionary<string, string> FileValues = LoadEnvFile();

    /// <summary>
    /// Returns the first non-empty value for <paramref name="keys"/>, checking the
    /// process environment first and then the .env file, in the order given.
    /// </summary>
    public static string? Get(params string[] keys)
    {
        foreach (var key in keys)
        {
            var env = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();
        }

        foreach (var key in keys)
        {
            if (FileValues.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static Dictionary<string, string> LoadEnvFile()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = FindEnvFile();
            if (path is null)
                return result;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith("export ", StringComparison.Ordinal))
                    line = line.Substring("export ".Length).Trim();

                var idx = line.IndexOf('=');
                if (idx <= 0)
                    continue;

                var key = line.Substring(0, idx).Trim();
                var value = line.Substring(idx + 1).Trim();

                // Strip a single pair of surrounding quotes, if present.
                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                {
                    value = value.Substring(1, value.Length - 2);
                }

                if (key.Length > 0)
                    result[key] = value;
            }
        }
        catch
        {
            // A missing or unreadable .env is fine; fall back to environment variables.
        }

        return result;
    }

    private static string? FindEnvFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        var cwd = Path.Combine(Environment.CurrentDirectory, ".env");
        return File.Exists(cwd) ? cwd : null;
    }
}
