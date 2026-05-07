using Microsoft.ML.Tokenizers;

namespace MemoryService.Recall;

public sealed class TokenCounter
{
    private readonly Tokenizer _tokenizer;
    private readonly double _safetyFactor;

    public TokenCounter(string chatModel, string llmProvider)
    {
        _tokenizer = chatModel switch
        {
            var m when m.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase) => TiktokenTokenizer.CreateForModel("gpt-4o"),
            var m when m.StartsWith("gpt-4",  StringComparison.OrdinalIgnoreCase) => TiktokenTokenizer.CreateForModel("gpt-4"),
            _ => TiktokenTokenizer.CreateForModel("gpt-4o-mini"),
        };

        // For non-OpenAI providers, cl100k_base is a proxy. Apply a 1.15x safety factor on counts.
        _safetyFactor = llmProvider.Equals("openai", StringComparison.OrdinalIgnoreCase) ? 1.0 : 1.15;
    }

    public int Count(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var raw = _tokenizer.CountTokens(text);
        return (int)Math.Ceiling(raw * _safetyFactor);
    }
}
