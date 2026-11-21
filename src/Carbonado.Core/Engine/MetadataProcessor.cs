using System.Text;
using System.Xml;
using System.Xml.Linq;
using Carbonado.Core.Decoding;
using Carbonado.Core.Encoders;
using Carbonado.Core.Imaging;

namespace Carbonado.Core.Engine;

/// <summary>Applies a <see cref="MetadataPolicy"/> to the metadata of a decoded image before it is encoded.</summary>
public static class MetadataProcessor
{
    /// <summary>
    /// Returns a filtered copy of <paramref name="m"/> (the input is never modified):
    /// <list type="bullet">
    /// <item><see cref="MetadataPolicy.StripAll"/>: no ICC, EXIF or XMP; density reset to 96 DPI.</item>
    /// <item><see cref="MetadataPolicy.KeepColorProfile"/>: the ICC profile only; density reset to 96 DPI.</item>
    /// <item><see cref="MetadataPolicy.KeepAllButGps"/>: everything except location: the EXIF GPS directory is erased
    /// (<see cref="ExifReader.StripGps"/>) and every XMP property whose name starts with "GPS" is removed (exif:GPS…,
    /// exifEX:GPS…, vendor Gps… fields). EXIF or XMP that cannot be parsed is dropped, since it cannot be proven clean.</item>
    /// <item><see cref="MetadataPolicy.KeepAll"/>: everything.</item>
    /// </list>
    /// The engine decodes pixels upright, so the result always has <see cref="ImageMetadata.Orientation"/> 1 and any
    /// kept EXIF has its orientation tag set to 1 (an EXIF block whose structure cannot be edited is dropped).
    /// </summary>
    public static ImageMetadata Apply(ImageMetadata m, MetadataPolicy p)
    {
        ArgumentNullException.ThrowIfNull(m);
        ImageMetadata result = m.Clone();
        result.Orientation = 1;
        switch (p)
        {
            case MetadataPolicy.StripAll:
                result.Icc = null;
                result.Exif = null;
                result.Xmp = null;
                ResetDensity(result);
                break;

            case MetadataPolicy.KeepColorProfile:
                result.Exif = null;
                result.Xmp = null;
                ResetDensity(result);
                break;

            case MetadataPolicy.KeepAllButGps:
                result.Exif = WithoutGps(result.Exif);
                result.Xmp = XmpGps.Strip(result.Xmp);
                break;

            case MetadataPolicy.KeepAll:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(p), p, null);
        }

        result.Icc = NullIfEmpty(result.Icc);
        result.Exif = Upright(NullIfEmpty(result.Exif));
        result.Xmp = NullIfEmpty(result.Xmp);
        return result;
    }

    private static void ResetDensity(ImageMetadata m)
    {
        m.DpiX = 96;
        m.DpiY = 96;
    }

    private static byte[]? NullIfEmpty(byte[]? data) => data is { Length: > 0 } ? data : null;

    private static byte[]? WithoutGps(byte[]? exif)
    {
        if (exif is null)
        {
            return null;
        }

        try
        {
            return ExifReader.StripGps(exif);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static byte[]? Upright(byte[]? exif)
    {
        if (exif is null || ExifReader.ReadOrientation(exif) == 1)
        {
            return exif;
        }

        try
        {
            return ExifReader.WithOrientation(exif, 1);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Removes GPS properties from an XMP packet.</summary>
    internal static class XmpGps
    {
        /// <summary>
        /// The packet without properties (attributes or elements, any namespace) whose local name starts with "GPS",
        /// ignoring case. The original bytes when there are none; null when the packet is not well-formed XML.
        /// </summary>
        public static byte[]? Strip(byte[]? xmp)
        {
            if (xmp is not { Length: > 0 })
            {
                return xmp;
            }

            XDocument document;
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 64L << 20,
                };
                using var reader = XmlReader.Create(new MemoryStream(xmp, writable: false), settings);
                document = XDocument.Load(reader, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return null;
            }

            var attributes = document.Descendants().Attributes().Where(a => !a.IsNamespaceDeclaration && IsGps(a.Name)).ToList();
            var elements = document.Descendants().Where(e => IsGps(e.Name)).ToList();
            if (attributes.Count == 0 && elements.Count == 0)
            {
                return xmp;
            }

            attributes.ForEach(a => a.Remove());

            // Outermost first: a nested GPS element leaves with its parent.
            foreach (XElement element in elements.Where(e => !e.Ancestors().Any(x => IsGps(x.Name))))
            {
                element.Remove();
            }

            using var output = new MemoryStream(xmp.Length);
            var writerSettings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                OmitXmlDeclaration = document.Declaration is null,
                NewLineHandling = NewLineHandling.None,
            };
            using (var writer = XmlWriter.Create(output, writerSettings))
            {
                document.Save(writer);
            }

            return output.ToArray();
        }

        private static bool IsGps(XName name) => name.LocalName.StartsWith("GPS", StringComparison.OrdinalIgnoreCase);
    }
}
