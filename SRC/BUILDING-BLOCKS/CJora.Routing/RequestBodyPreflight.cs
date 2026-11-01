using Microsoft.AspNetCore.Http;
using System.Buffers;
using CJora.Routing;

namespace CJora.Routing;

public static class RequestBodyPreflight
{
    /// <summary>Unknown-length uploads are bounded before dispatch; temporary buffering is disposed by ASP.NET.</summary>
    public static async Task BufferUnknownLengthAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength.HasValue) return;
        request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: (long)limit + 64 * 1024);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer.AsMemory(0, 64 * 1024), cancellationToken)) > 0)
            {
                total += read;
                if (total > limit) throw new RouteRejectedException(413, "REQUEST_TOO_LARGE", "Request exceeds the method size limit.");
            }
            request.Body.Position = 0;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}