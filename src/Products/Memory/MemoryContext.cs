using DataEntities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.InMemory;
using Newtonsoft.Json;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Products.Models;
using SearchEntities;
using System.Text;
using VectorEntities;

namespace Products.Memory;

public class MemoryContext
{
    private const string SystemPrompt = "You are a useful assistant. You always reply with a short and funny message. If you do not know an answer, you say 'I don't know that.' You only answer questions related to outdoor camping products. For any other type of questions, explain to the user that you only answer outdoor camping products questions. Do not store memory of the chat conversation.";

    private readonly ILogger _logger;
    private readonly ChatClient? _chatClient;
    private readonly EmbeddingClient? _embeddingClient;
    private IVectorStoreRecordCollection<int, ProductVector>? _productsCollection;
    private bool _isMemoryCollectionInitialized;

    public MemoryContext(ILogger logger, ChatClient? chatClient, EmbeddingClient? embeddingClient)
    {
        _logger = logger;
        _chatClient = chatClient;
        _embeddingClient = embeddingClient;

        _logger.LogInformation("Memory context created");
        _logger.LogInformation($"Chat Client is null: {_chatClient is null}");
        _logger.LogInformation($"Embedding Client is null: {_embeddingClient is null}");
    }

    public async Task<bool> InitMemoryContextAsync(Context db)
    {
        if (_isMemoryCollectionInitialized)
        {
            _logger.LogInformation("Memory context already initialized");
            return true;
        }

        _logger.LogInformation("Initializing memory context");
        var vectorProductStore = new InMemoryVectorStore();
        _productsCollection = vectorProductStore.GetCollection<int, ProductVector>("products");
        await _productsCollection.CreateCollectionIfNotExistsAsync();

        _logger.LogInformation("Get a copy of the list of products");
        var products = await db.Product.ToListAsync();

        _logger.LogInformation("Filling products in memory");

        // iterate over the products and add them to the memory
        foreach (var product in products)
        {
            try
            {
                _logger.LogInformation("Adding product to memory: {Product}", product.Name);
                var productInfo = $"[{product.Name}] is a product that costs [{product.Price}] and is described as [{product.Description}]";

                // new product vector
                var productVector = new ProductVector
                {
                    Id = product.Id,
                    Name = product.Name,
                    Description = product.Description,
                    Price = product.Price,
                    ImageUrl = product.ImageUrl
                };
                var result = await _embeddingClient!.GenerateEmbeddingAsync(productInfo);

                productVector.Vector = result.Value.ToFloats();
                var recordId = await _productsCollection.UpsertAsync(productVector);
                _logger.LogInformation("Product added to memory: {Product} with recordId: {RecordId}", product.Name, recordId);
            }
            catch (Exception exc)
            {
                _logger.LogError(exc, $"Error adding product {product.Name} to memory");
                _isMemoryCollectionInitialized = false;
                return false;
            }
        }

        _isMemoryCollectionInitialized = true;
        _logger.LogInformation("DONE! Filling products in memory");
        return true;
    }

    public async Task<SearchResponse> Search(string search, Context db)
    {
        if (!_isMemoryCollectionInitialized)
        {
            await InitMemoryContextAsync(db);
        }

        var response = new SearchResponse
        {
            Response = $"I don't know the answer for your question. Your question is: [{search}]"
        };
        try
        {
            var result = await _embeddingClient!.GenerateEmbeddingAsync(search);
            var vectorSearchQuery = result.Value.ToFloats();

            var searchOptions = new VectorSearchOptions<ProductVector>
            {
                Top = 2
            };

            // search the vector database for the most similar product        
            var searchResults = await _productsCollection!.VectorizedSearchAsync(vectorSearchQuery, searchOptions);
            var sbFoundProducts = new StringBuilder();
            int productPosition = 1;
            await foreach (var searchItem in searchResults.Results)
            {
                if (searchItem.Score > 0.5)
                {
                    var foundProduct = await db.FindAsync<Product>(searchItem.Record.Id);
                    if (foundProduct != null)
                    {
                        response.Products.Add(foundProduct);
                        sbFoundProducts.AppendLine($"- Product {productPosition}:");
                        sbFoundProducts.AppendLine($"  - Name: {foundProduct.Name}");
                        sbFoundProducts.AppendLine($"  - Description: {foundProduct.Description}");
                        sbFoundProducts.AppendLine($"  - Price: {foundProduct.Price}");
                        productPosition++;
                    }
                }
            }

            // let's improve the response message
            var prompt = @$"You are an intelligent assistant helping clients with their search about outdoor products. 
Generate a catchy and friendly message using the information below.
Add a comparison between the products found and the search criteria.
Include products details.
    - User Question: {search}
    - Found Products: 
{sbFoundProducts}";

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(SystemPrompt),
                new UserChatMessage(prompt)
            };

            _logger.LogInformation("{ChatHistory}", JsonConvert.SerializeObject(messages));

            var resultPrompt = await _chatClient!.CompleteChatAsync(messages);
            response.Response = resultPrompt.Value.Content[0].Text!;
        }
        catch (Exception ex)
        {
            // Handle exceptions (log them, rethrow, etc.)
            response.Response = $"An error occurred: {ex.Message}";
        }
        return response;
    }
}
