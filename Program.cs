using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "YtTranscribeApi", Version = "v1" });
});

builder.Services.AddHttpClient();

builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 1024L * 1024L * 1024L;
});

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 1024L * 1024L * 1024L;
});

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "YtTranscribeApi v1");
});

app.MapControllers();
app.Run();
