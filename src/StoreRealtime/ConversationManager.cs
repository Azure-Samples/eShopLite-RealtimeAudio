using DataEntities;
using Microsoft.Extensions.AI;
using OpenAI.RealtimeConversation;
using SearchEntities;
using StoreRealtime.Components;
using StoreRealtime.ContextManagers;
using StoreRealtime.Support;
using System.Text;
using System.Text.Json;

namespace StoreRealtime;

public class ConversationManager(
    RealtimeConversationClient client, 
    ContosoProductContext contosoProductContext,
    ILogger logger) : IDisposable
{
    private RealtimeConversationSession? session;

    ContosoProductContext _contosoProductContext = contosoProductContext;

    public async Task RunAsync(Stream audioInput, Speaker audioOutput, 
        Func<string, Task> addMessageAsync, 
        Func<string, bool, Task> addChatMessageAsync,
        Func<List<Product>, Task> addChatProductMessageAsync,
        CancellationToken cancellationToken)
    {
        var prompt = $"""
            You are a useful assistant.
            Respond as succinctly as possible, in just a few words.
            Your main field of expertise is outdoor products.
            You are able to answer questions about outdoor products, including their features, specifications, and availability.
            Check the product database and external sources for information.
            The current date is {DateTime.Now.ToLongDateString()}
            """;
        await addMessageAsync("Connecting...");
        await addChatMessageAsync("Hello, how can I help?", false);

        var contosoSemanticSearchTool = 
            AIFunctionFactory.Create(_contosoProductContext.SemanticSearchOutdoorProductsAsync);
        var contosoSearchByProductNameTool = 
            AIFunctionFactory.Create(_contosoProductContext.SearchOutdoorProductsByNameAsync);

        List<AIFunction> tools = [contosoSemanticSearchTool, contosoSearchByProductNameTool];

        var sessionOptions = new ConversationSessionOptions()
        {
            Instructions = prompt,
            Voice = ConversationVoice.Shimmer,
            InputTranscriptionOptions = new() { Model = "whisper-1" }
        };

        foreach (var tool in tools)
        {
            sessionOptions.Tools.Add(tool.ToConversationFunctionTool());
        }

        session = await client.StartConversationSessionAsync(cancellationToken);
        await session.ConfigureSessionAsync(sessionOptions);

        var outputTranscription = new StringBuilder();

        await foreach (ConversationUpdate update in session.ReceiveUpdatesAsync(cancellationToken))
        {
            switch (update)
            {
                case ConversationSessionStartedUpdate:
                    await addMessageAsync("Conversation started");                    
                    _ = Task.Run(async () => await session.SendInputAudioAsync(audioInput, cancellationToken));
                    break;

                case ConversationInputSpeechStartedUpdate:
                    await addMessageAsync("Speech started");
                    await audioOutput.ClearPlaybackAsync();
                    break;

                case ConversationInputTranscriptionFinishedUpdate:
                    var transcript = update as ConversationInputTranscriptionFinishedUpdate;
                    await addMessageAsync($"User: {transcript.Transcript}");
                    await addChatMessageAsync(transcript.Transcript, true);
                    break;

                case ConversationInputSpeechFinishedUpdate:
                    await addMessageAsync("Speech finished");
                    break;

                case ConversationItemStreamingPartDeltaUpdate outputDelta:
                    await audioOutput.EnqueueAsync(outputDelta.AudioBytes?.ToArray());
                    outputTranscription.Append(outputDelta.Text ?? outputDelta.AudioTranscript);
                    break;

                case ConversationItemStreamingAudioTranscriptionFinishedUpdate:
                case ConversationItemStreamingTextFinishedUpdate:
                    await addMessageAsync($"Assistant: {outputTranscription}");
                    await addChatMessageAsync($"{outputTranscription}", false);
                    outputTranscription.Clear();
                    break;

                case ConversationItemStreamingFinishedUpdate itemFinished:
                    if (!string.IsNullOrEmpty(itemFinished.FunctionName))
                    {

                        await addMessageAsync($"Calling function: {itemFinished.FunctionName}({itemFinished.FunctionCallArguments})");
                        if (await itemFinished.GetFunctionCallOutputAsync(tools) is { } output)
                        {
                            await addMessageAsync($"Call function finished: {output.ToString()}");
                            await session.AddItemAsync(output);

                            // Use reflection to access the hidden "Output" property
                            var outputProperty = output.GetType().GetProperty("Output",
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic |
                                System.Reflection.BindingFlags.Instance);

                            if (outputProperty != null)
                            {
                                var outputValue = outputProperty.GetValue(output);
                                if (outputValue != null)
                                {
                                    SearchResponse response = JsonSerializer.Deserialize<SearchResponse>(outputValue.ToString());
                                    await addChatProductMessageAsync(response.Products);
                                }
                            }
                        }
                    }
                    break;

                case ConversationResponseFinishedUpdate responseFinished:
                    // If we added one or more function call results, instruct the model to respond to them
                    if (responseFinished.CreatedItems.Any(item => !string.IsNullOrEmpty(item.FunctionName)))
                    {
                        StringBuilder tokens = new();
                        tokens.AppendLine($"Total Tokens: {responseFinished.Usage.TotalTokens}");
                        tokens.AppendLine($"Input Tokens: {responseFinished.Usage.InputTokens}");
                        tokens.AppendLine($"Output Tokens: {responseFinished.Usage.OutputTokens}");
                        await addMessageAsync($"{tokens.ToString()}");

                        await session.StartResponseAsync();
                    }
                    break;
            }
        }
    }

    public void Dispose()
        => session?.Dispose();

}
