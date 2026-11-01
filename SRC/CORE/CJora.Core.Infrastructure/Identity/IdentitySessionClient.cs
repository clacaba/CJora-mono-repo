using System.Net.Http.Json;
using CJora.Core.Application.Identity;
using CJora.Identity.Contracts;

namespace CJora.Core.Infrastructure.Identity;

public sealed class IdentitySessionClient(HttpClient client,string token) : IIdentitySessionClient
{
    public async Task<ValidatedIdentity> ValidateAsync(ValidateSession request,CancellationToken cancellationToken)
    {
        using var response=await Send("internal/sessions/validate",request,cancellationToken);
        return await response.Content.ReadFromJsonAsync<ValidatedIdentity>(cancellationToken:cancellationToken) ?? throw new IdentityFailure(503,"IDENTITY_UNAVAILABLE");
    }
    public async Task RevokeAsync(RevokeSession request,CancellationToken cancellationToken)
    { using var response=await Send("internal/sessions/revoke",request,cancellationToken); }
    private async Task<HttpResponseMessage> Send<T>(string path,T value,CancellationToken cancellationToken)
    {
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,path) {Content=JsonContent.Create(value)};
            request.Headers.Add(IdentityProtocol.CallerHeader,token);
            var response=await client.SendAsync(request,cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var status=(int)response.StatusCode; response.Dispose();
                throw new IdentityFailure(status is 401 or 403?status:503,status==401?"AUTH_SESSION_INVALID":status==403?"MEMBERSHIP_DENIED":"IDENTITY_UNAVAILABLE");
            }
            return response;
        }
        catch (IdentityFailure) { throw; }
        catch { throw new IdentityFailure(503,"IDENTITY_UNAVAILABLE"); }
    }
}