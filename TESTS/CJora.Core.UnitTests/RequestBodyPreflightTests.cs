using CJora.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace CJora.Core.UnitTests;

public sealed class RequestBodyPreflightTests
{
    [Fact]
    public async Task Unknown_length_body_is_rewound_without_changing_its_bytes()
    {
        var context = new DefaultHttpContext();
        var payload = new byte[] { 0, 255, 13, 10, 42 };
        context.Request.Body = new MemoryStream(payload);
        await RequestBodyPreflight.BufferUnknownLengthAsync(context.Request, 1024, CancellationToken.None);
        Assert.Equal(0, context.Request.Body.Position);
        using var copy = new MemoryStream();
        await context.Request.Body.CopyToAsync(copy);
        Assert.Equal(payload, copy.ToArray());
    }

    [Fact]
    public async Task Unknown_length_body_cannot_bypass_the_operation_size_limit()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[1025]);
        var exception = await Assert.ThrowsAsync<RouteRejectedException>(() =>
            RequestBodyPreflight.BufferUnknownLengthAsync(context.Request, 1024, CancellationToken.None));
        Assert.Equal(413, exception.StatusCode);
    }

    [Fact]
    public async Task Known_length_body_remains_unread_for_streaming()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[512]);
        context.Request.ContentLength = 512;
        await RequestBodyPreflight.BufferUnknownLengthAsync(context.Request, 1024, CancellationToken.None);
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public void Multipart_value_providers_are_removed_before_MVC_can_consume_the_proxy_body()
    {
        var factories = new List<IValueProviderFactory>
        {
            new FormValueProviderFactory(), new FormFileValueProviderFactory(),
            new JQueryFormValueProviderFactory(), new RouteValueProviderFactory(), new QueryStringValueProviderFactory()
        };
        var context = new ResourceExecutingContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(), factories);
        new PreserveRequestBodyAttribute().OnResourceExecuting(context);
        Assert.Equal(2, factories.Count);
        Assert.Contains(factories, factory => factory is RouteValueProviderFactory);
        Assert.Contains(factories, factory => factory is QueryStringValueProviderFactory);
    }
}