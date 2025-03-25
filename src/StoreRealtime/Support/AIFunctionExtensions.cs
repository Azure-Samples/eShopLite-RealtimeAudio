using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.RealtimeConversation;

namespace StoreRealtime.Support;

public static class AIFunctionExtensions
{
    /// <summary>
    /// Converts a <see cref="AIFunction"/> into a <see cref="ConversationFunctionTool"/> so that
    /// it can be used with <see cref="RealtimeConversationClient"/>.
    /// </summary>
    public static ConversationFunctionTool ToConversationFunctionTool(this AIFunction aiFunction)
    {
        return new ConversationFunctionTool()
        {
            Name = aiFunction.Metadata.Name,
            Description = aiFunction.Metadata.Description,
            Parameters = BinaryData.FromString(
                $$"""
                {
                    "type": "object",
                    "properties": {
                        {{string.Join(',', aiFunction.Metadata.Parameters.Select(p => $$"""
                            "{{p.Name}}": {{p.Schema}}
                        """))}}
                    },
                    "required": {{JsonSerializer.Serialize(aiFunction.Metadata.Parameters.Where(p => p.IsRequired).Select(p => p.Name))}}
                }
                """)
        };
    }

    public static async Task<ConversationItem?> GetFunctionCallOutputAsync(this ConversationItemStreamingFinishedUpdate update, IReadOnlyList<AIFunction> tools)
    {
        if (!string.IsNullOrEmpty(update.FunctionName) && tools.FirstOrDefault(t => t.Metadata.Name == update.FunctionName) is AIFunction aiFunction)
        {
            Dictionary<string, object?>? jsonArgs = null;
            try
            {
                jsonArgs = JsonSerializer.Deserialize<Dictionary<string, object?>>(update.FunctionCallArguments)!;
                var output = await aiFunction.InvokeAsync(jsonArgs);
                var ci = ConversationItem.CreateFunctionCallOutput(update.FunctionCallId, output?.ToString() ?? "");
                return ci;
            }
            catch (JsonException)
            {
                return ConversationItem.CreateFunctionCallOutput(update.FunctionCallId, "Invalid JSON");
            }
            catch
            {
                return ConversationItem.CreateFunctionCallOutput(update.FunctionCallId, "Error calling tool");
            }
        }

        return null;
    }
}
