using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SnapBoard.Sync.WebDav;

internal static class WebDavPropFindParser
{
    private const int MaximumXmlDepth = 16;
    private const int MaximumAttributesPerElement = 16;
    private const int MaximumElements = 50_000;
    private static readonly XNamespace Dav = "DAV:";

    public static IReadOnlyList<WebDavResource> Parse(
        ReadOnlySpan<byte> xml,
        Uri rootUri,
        Uri collectionUri,
        int maximumCharacters,
        int maximumHrefCount)
    {
        try
        {
            using MemoryStream stream = new(xml.ToArray(), writable: false);
            XmlReaderSettings settings = new()
            {
                Async = false,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = maximumCharacters,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using XmlReader reader = XmlReader.Create(stream, settings);
            XDocument document = XDocument.Load(reader, LoadOptions.None);
            ValidateShape(document);
            if (document.Root?.Name != Dav + "multistatus")
            {
                throw new WebDavProtocolException("PROPFIND response is not DAV multistatus XML.");
            }

            List<WebDavResource> resources = [];
            foreach (XElement response in document.Root.Elements(Dav + "response"))
            {
                if (resources.Count >= maximumHrefCount)
                {
                    throw new WebDavProtocolException("PROPFIND returned too many href values.");
                }

                XElement[] hrefs = response.Elements(Dav + "href").Take(2).ToArray();
                if (hrefs.Length != 1 || !WebDavPathPolicy.TryResolveHref(
                        rootUri,
                        collectionUri,
                        hrefs[0].Value,
                        out WebDavResourceLocation location))
                {
                    throw new WebDavProtocolException("PROPFIND returned an unsafe href.");
                }

                XElement? successfulProp = response
                    .Elements(Dav + "propstat")
                    .FirstOrDefault(IsSuccessfulPropStat)?
                    .Element(Dav + "prop");
                string? etag = successfulProp?.Element(Dav + "getetag")?.Value;
                long? contentLength = TryParseContentLength(
                    successfulProp?.Element(Dav + "getcontentlength")?.Value);
                bool isCollection = successfulProp?
                    .Element(Dav + "resourcetype")?
                    .Element(Dav + "collection") is not null;
                resources.Add(new WebDavResource(
                    location.RelativePath,
                    location.ObjectName,
                    isCollection,
                    NormalizeEtag(etag),
                    contentLength));
            }

            return resources;
        }
        catch (WebDavProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new WebDavProtocolException("PROPFIND XML is malformed or exceeds limits.", exception);
        }
    }

    private static bool IsSuccessfulPropStat(XElement propStat)
    {
        string? status = propStat.Element(Dav + "status")?.Value;
        return status is not null && status.Contains(" 200 ", StringComparison.Ordinal);
    }

    private static long? TryParseContentLength(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) &&
        parsed >= 0
            ? parsed
            : null;

    private static string? NormalizeEtag(string? etag)
    {
        string? value = etag?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > 256 ? null : value;
    }

    private static void ValidateShape(XDocument document)
    {
        if (document.Root is null)
        {
            throw new WebDavProtocolException("PROPFIND XML has no root element.");
        }

        Stack<(XElement Element, int Depth)> pending = new();
        pending.Push((document.Root, 1));
        int elements = 0;
        while (pending.TryPop(out (XElement Element, int Depth) current))
        {
            elements++;
            if (elements > MaximumElements || current.Depth > MaximumXmlDepth ||
                current.Element.Attributes().Take(MaximumAttributesPerElement + 1).Count() >
                MaximumAttributesPerElement)
            {
                throw new WebDavProtocolException("PROPFIND XML shape exceeds limits.");
            }

            foreach (XElement child in current.Element.Elements())
            {
                pending.Push((child, current.Depth + 1));
            }
        }
    }
}
