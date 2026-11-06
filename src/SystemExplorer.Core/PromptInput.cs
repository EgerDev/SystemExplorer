namespace SystemExplorer.Core;

public static class PromptInput
{
    public static string? Normalize(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public static bool CanSubmit(string? text) => Normalize(text) is not null;
}
