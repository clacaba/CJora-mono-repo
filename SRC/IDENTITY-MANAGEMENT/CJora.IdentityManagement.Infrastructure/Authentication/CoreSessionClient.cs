using System.Net.Http.Json;
using CJora.Identity.Contracts;
using CJora.IdentityManagement.Application.Authentication;

namespace CJora.IdentityManagement.Infrastructure.Authentication;

public sealed class CoreSessionClient(HttpClient client,string token) : ICoreSessionClient
{
    public async Task<CoreSessionReference> PrepareAsync(PrepareSession request,CancellationToken ct)
    { using var response=await Send("internal/sessions/prepare",request,ct); return await response.Content.ReadFromJsonAsync<CoreSessionReference>(cancellationToken:ct) ?? throw new IdentityFailure(503,"CORE_UNAVAILABLE"); }
    public async Task<SessionCookie> ActivateAsync(ActivateSession request,CancellationToken ct)
    { using var response=await Send("internal/sessions/activate",request,ct); return await response.Content.ReadFromJsonAsync<SessionCookie>(cancellationToken:ct) ?? throw new IdentityFailure(503,"CORE_UNAVAILABLE"); }
    public async Task RevokeAsync(RevokeSession request,CancellationToken ct) { using var response=await Send("internal/sessions/revoke",request,ct); }
    private async Task<HttpResponseMessage> Send<T>(string path,T value,CancellationToken ct)
    {
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,path) {Content=JsonContent.Create(value)};
            request.Headers.Add(IdentityProtocol.CallerHeader,token);
            var response=await client.SendAsync(request,ct);
            if (!response.IsSuccessStatusCode) { var status=(int)response.StatusCode; response.Dispose(); throw new IdentityFailure(status is 401 or 403?status:503,"CORE_SESSION_UNAVAILABLE"); }
            return response;
        }
        catch (IdentityFailure) { throw; }
        catch { throw new IdentityFailure(503,"CORE_UNAVAILABLE"); }
    }
}