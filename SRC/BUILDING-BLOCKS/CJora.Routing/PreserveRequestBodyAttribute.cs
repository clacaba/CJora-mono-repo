using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CJora.Routing;

/// <summary>MVC must not parse multipart forms before YARP streams the original request body.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PreserveRequestBodyAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        context.ValueProviderFactories.RemoveType<FormValueProviderFactory>();
        context.ValueProviderFactories.RemoveType<FormFileValueProviderFactory>();
        context.ValueProviderFactories.RemoveType<JQueryFormValueProviderFactory>();
    }
    public void OnResourceExecuted(ResourceExecutedContext context) { }
}