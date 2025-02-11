using StoreRealtime.Components;
using StoreRealtime.Services;
using StoreRealtime.ContextManagers;
using Azure.AI.OpenAI;
using System.ClientModel;
using OpenAI.RealtimeConversation;
using Azure;
using System.Text.RegularExpressions;
using System.Web;

var builder = WebApplication.CreateBuilder(args);

// add aspire service defaults
builder.AddServiceDefaults();

builder.Services.AddSingleton<ProductService>();
builder.Services.AddHttpClient<ProductService>(
    static client => client.BaseAddress = new("https+http://products"));

var azureOpenAiClientName = "openai";
string? aoaiCnnString = builder.Configuration.GetConnectionString("openai");
//var aoaiEndpoint = aoaiCnnString?.Split("Endpoint=")[1].Split("/openai/")[0];
var aoaiEndpoint = aoaiCnnString != null ?
    Regex.Match(aoaiCnnString, @"Endpoint=(https://[^\s;]+)").Groups[1].Value : null;

builder.AddAzureOpenAIClient(azureOpenAiClientName,
    settings =>
    {
        settings.DisableMetrics = false;
        settings.DisableTracing = false;
        settings.Endpoint = new Uri(aoaiEndpoint);
    });

// get azure openai client and create Chat client from aspire hosting configuration
builder.Services.AddSingleton(serviceProvider =>
{
    var chatDeploymentName = "gpt-4o-realtime-preview";
    var logger = serviceProvider.GetService<ILogger<Program>>()!;
    logger.LogInformation($"Realtime Chat client configuration, modelId: {chatDeploymentName}");

    var config = serviceProvider.GetService<IConfiguration>()!;
    RealtimeConversationClient realtimeConversationClient = null;
    try
    {
        AzureOpenAIClient client = serviceProvider.GetRequiredService<AzureOpenAIClient>();
        realtimeConversationClient = client.GetRealtimeConversationClient(chatDeploymentName);
        logger.LogInformation($"Realtime Chat client created, modelId: {realtimeConversationClient.ToString()}");
    }
    catch (Exception exc)
    {
        logger.LogError(exc, "Error creating realtime conversation client");
    }
    return realtimeConversationClient;
});

builder.Services.AddSingleton<IConfiguration>(sp =>
{
    return builder.Configuration;
});

builder.Services.AddSingleton(serviceProvider =>
{
    ProductService productService = serviceProvider.GetRequiredService<ProductService>();
    return new ContosoProductContext(productService);
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// aspire map default endpoints
app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// log values for the AOAI services
app.Logger.LogInformation($@"========================================
Azure OpenAI information
Azure OpenAI Connection String: {aoaiCnnString}
Azure OpenAI Endpoint: {aoaiEndpoint}
========================================");

app.Run();


static (string endpoint, string apiKey) GetEndpointAndKey(WebApplicationBuilder builder, string name)
{
    var connectionString = builder.Configuration.GetConnectionString(name);
    var parameters = HttpUtility.ParseQueryString(connectionString.Replace(";", "&"));
    return (parameters["Endpoint"], parameters["Key"]);
}