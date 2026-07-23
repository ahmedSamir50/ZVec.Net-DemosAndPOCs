using System.Text.Json.Serialization;

namespace PDDM.Core.Models.LmStudio;

public sealed class EmbeddingRequest
{
    public string Model { get; set; } = "";
    public List<string> Input { get; set; } = [];
}

public sealed class EmbeddingResponse
{
    public List<EmbeddingData> Data { get; set; } = [];
}

public sealed class EmbeddingData
{
    public List<float> Embedding { get; set; } = [];
    public int Index { get; set; }
}

public sealed class ChatCompletionRequest
{
    public string Model { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = [];
    public float Temperature { get; set; } = 0.3f;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = -1;

    public bool Stream { get; set; }
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class ChatStreamChunk
{
    public List<ChatStreamChoice> Choices { get; set; } = [];
}

public sealed class ChatStreamChoice
{
    public ChatStreamDelta? Delta { get; set; }
    public string? FinishReason { get; set; }
}

public sealed class ChatStreamDelta
{
    public string? Role { get; set; }
    public string? Content { get; set; }
}

public sealed class ChatCompletionResponse
{
    public List<ChatChoice> Choices { get; set; } = [];
}

public sealed class ChatChoice
{
    public ChatMessage Message { get; set; } = new();
}

public sealed class ModelsResponse
{
    public List<ModelInfo> Data { get; set; } = [];
}

public sealed class ModelInfo
{
    public string Id { get; set; } = "";
}
