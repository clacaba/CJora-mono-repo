using Microsoft.Extensions.FileProviders;

namespace CJora.Gateway.Api;

public static class WebFrontend
{
    public static void MapWebFrontend(this WebApplication app)
    {
        var directory=Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath,"..","..","WEB","CJora.Web","dist"));
        if (!Directory.Exists(directory)) return;
        app.UseWhen(context=>context.Request.Path.StartsWithSegments("/assets") ||
            context.Request.Path.StartsWithSegments("/app") || context.Request.Path=="/",
            branch=>branch.Use(async(context,next)=>
            {
                context.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
                context.Response.Headers["X-Content-Type-Options"]="nosniff";
                context.Response.Headers["Referrer-Policy"]="no-referrer";
                context.Response.Headers.CacheControl="no-store";
                await next();
            }));
        var assets=Path.Combine(directory,"assets");
        if(Directory.Exists(assets))
            app.UseStaticFiles(new StaticFileOptions{FileProvider=new PhysicalFileProvider(assets),RequestPath="/assets"});
        foreach(var route in new[]{"/","/app","/app/login-failed"})
            app.MapGet(route,()=>Results.File(Path.Combine(directory,"index.html"),"text/html"));
    }
}
