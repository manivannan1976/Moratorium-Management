using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;

namespace MoratoriumManagement.Api.Api;

public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

/// <summary>
/// Lossless JSON &lt;-&gt; XML mapping so every response can be consumed as XML (Duck Creek) and the
/// evaluation request can be posted as XML. Arrays become a wrapper element containing singular children,
/// e.g. <c>locations</c> holds repeated <c>location</c> elements.
/// </summary>
public static class JsonXml
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private static readonly Dictionary<string, string> Singular = new(StringComparer.OrdinalIgnoreCase)
    {
        ["locations"] = "location",
        ["affectedLocations"] = "affectedLocation",
        ["moratoriums"] = "moratorium",
        ["allMatchedCriteria"] = "criteria",
        ["validationErrors"] = "validationError",
        ["areas"] = "area",
        ["counties"] = "county",
        ["zipCodes"] = "zipCode",
        ["items"] = "item",
        ["data"] = "data",
    };

    private static readonly HashSet<string> ArrayNames = new(
        Singular.Where(kv => kv.Key != "data").Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);

    public static XDocument ToXml(JsonNode? node, string rootName)
    {
        var root = new XElement(rootName);
        Fill(root, node, rootName);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static void Fill(XElement el, JsonNode? node, string name)
    {
        switch (node)
        {
            case null:
                el.Add(new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName), new XAttribute(Xsi + "nil", "true"));
                break;
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    var child = new XElement(XmlConvert.EncodeLocalName(key));
                    Fill(child, value, key);
                    el.Add(child);
                }
                break;
            case JsonArray arr:
                var itemName = Singular.GetValueOrDefault(name, "item");
                foreach (var item in arr)
                {
                    var child = new XElement(itemName);
                    Fill(child, item, itemName);
                    el.Add(child);
                }
                break;
            default:
                var v = node.GetValue<JsonElement>();
                el.Value = v.ValueKind switch
                {
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => v.ToString(),
                };
                break;
        }
    }

    public static JsonNode? FromXml(XElement el)
    {
        if (el.Attribute(Xsi + "nil")?.Value == "true") return null;
        if (ArrayNames.Contains(el.Name.LocalName))
        {
            var arr = new JsonArray();
            foreach (var child in el.Elements()) arr.Add(FromXml(child));
            return arr;
        }
        if (el.HasElements)
        {
            var obj = new JsonObject();
            foreach (var child in el.Elements()) obj[child.Name.LocalName] = FromXml(child);
            return obj;
        }
        var text = el.Value;
        return text switch
        {
            "true" => JsonValue.Create(true),
            "false" => JsonValue.Create(false),
            _ => JsonValue.Create(text),
        };
    }

    public static string RootName(Type t)
    {
        var n = t.Name;
        var tick = n.IndexOf('`');
        if (tick >= 0) n = n[..tick];
        return char.ToLowerInvariant(n[0]) + n[1..];
    }
}

public sealed class XmlOutputFormatter : TextOutputFormatter
{
    public XmlOutputFormatter()
    {
        SupportedMediaTypes.Add("application/xml");
        SupportedMediaTypes.Add("text/xml");
        SupportedEncodings.Add(Encoding.UTF8);
    }

    public override async Task WriteResponseBodyAsync(OutputFormatterWriteContext context, Encoding selectedEncoding)
    {
        var node = JsonSerializer.SerializeToNode(context.Object, context.ObjectType ?? context.Object!.GetType(), Json.Options);
        var doc = JsonXml.ToXml(node, JsonXml.RootName(context.Object!.GetType()));
        await using var writer = new StreamWriter(context.HttpContext.Response.Body, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteAsync(doc.Declaration + Environment.NewLine + doc.Root);
    }
}

public sealed class XmlInputFormatter : TextInputFormatter
{
    public XmlInputFormatter()
    {
        SupportedMediaTypes.Add("application/xml");
        SupportedMediaTypes.Add("text/xml");
        SupportedEncodings.Add(Encoding.UTF8);
    }

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
    {
        try
        {
            var doc = await XDocument.LoadAsync(context.HttpContext.Request.Body, LoadOptions.None, context.HttpContext.RequestAborted);
            var node = JsonXml.FromXml(doc.Root!);
            var model = node.Deserialize(context.ModelType, Json.Options);
            return InputFormatterResult.Success(model);
        }
        catch (Exception)
        {
            return InputFormatterResult.Failure();
        }
    }
}
