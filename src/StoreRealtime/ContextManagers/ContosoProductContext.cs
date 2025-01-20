﻿using StoreRealtime.Services;
using System.ComponentModel;

namespace StoreRealtime.ContextManagers;

public class ContosoProductContext(ProductService productService)
{
    [Description("Search outdoor products in the Contoso database based on a search criteria provided by the user using natural language. The response will include the product name, description and price.")]
    public async Task<string> SemanticSearchOutdoorProductsAsync(string searchCriteria)
    {
        var response = await productService.Search(searchCriteria, true);
        return response.Response!;
    }

    [Description("Search outdoor products in the Contoso database based on a search criteria searching byt the product name. The response will include the product name, description and price.")]
    public async Task<string> SearchOutdoorProductsByNameAsync(string name)
    {
        var response = await productService.Search(name, false);
        return response.Response!;
    }
}