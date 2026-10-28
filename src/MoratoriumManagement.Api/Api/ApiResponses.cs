using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MoratoriumManagement.Api.Api;

public static class ApiResponses
{
    /// <summary>Writes a body outside MVC (auth failures), honouring Accept: application/xml.</summary>
    public static async Task WriteAsync(HttpContext ctx, int status, object body)
    {
        ctx.Response.StatusCode = status;
        var node = JsonSerializer.SerializeToNode(body, body.GetType(), Json.Options);
        if (WantsXml(ctx.Request))
        {
            ctx.Response.ContentType = "application/xml; charset=utf-8";
            var doc = JsonXml.ToXml(node, JsonXml.RootName(body.GetType()));
            await ctx.Response.WriteAsync(doc.Declaration + Environment.NewLine + doc.Root);
        }
        else
        {
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(node!.ToJsonString(Json.Options));
        }
    }

    public static bool WantsXml(HttpRequest r)
    {
        if (r.Query.TryGetValue("format", out var f)) return f.ToString().Equals("xml", StringComparison.OrdinalIgnoreCase);
        var accept = r.Headers.Accept.ToString();
        return accept.Contains("xml", StringComparison.OrdinalIgnoreCase) && !accept.Contains("json", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Maps ApiException to the standard error body; anything else becomes SYSTEM_ERROR.</summary>
public sealed class ApiExceptionFilter(ILogger<ApiExceptionFilter> log) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        ErrorResponse body;
        int status;
        if (context.Exception is ApiException ex)
        {
            status = ex.Status;
            body = new ErrorResponse { ErrorCode = ex.Code, Message = ex.Message, ValidationErrors = ex.Errors };
        }
        else
        {
            log.LogError(context.Exception, "Unhandled error in {Path}", context.HttpContext.Request.Path);
            status = 500;
            body = new ErrorResponse { ErrorCode = ErrorCodes.SystemError, Message = Msg.SystemError };
        }
        context.Result = new ObjectResult(body) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
