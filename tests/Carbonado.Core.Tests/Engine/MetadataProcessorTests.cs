using System.Text;
using Carbonado.Core.Decoding;
using Carbonado.Core.Encoders;
using Carbonado.Core.Engine;
using Carbonado.Core.Imaging;
using Carbonado.Core.Tests.Codecs;
using Carbonado.Core.Tests.Decoding;

namespace Carbonado.Core.Tests.Engine;

public class MetadataProcessorTests
{
    private static readonly byte[] GpsXmp = Encoding.UTF8.GetBytes(
        "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n"
        + "  <rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:exif=\"http://ns.adobe.com/exif/1.0/\" xmlns:drone-dji=\"http://www.dji.com/drone-dji/1.0/\""
        + " exif:GPSLatitude=\"45,27.5N\" drone-dji:GpsLongitude=\"9.18\" exif:ExposureTime=\"1/250\">\n"
        + "   <dc:format>image/jpeg</dc:format>\n   <exif:GPSLongitude>9,11.2E</exif:GPSLongitude>\n"
        + "   <exif:GPSAltitude><rdf:Description><exif:GPSAltitudeRef>0</exif:GPSAltitudeRef></rdf:Description></exif:GPSAltitude>\n"
        + "  </rdf:Description>\n </rdf:RDF>\n</x:xmpmeta>\n                                   \n<?xpacket end=\"w\"?>");

    private static ImageMetadata Full() => new()
    {
        Icc = TestIcc.DisplayP3Gamma22(),
        Exif = ExifTestData.Build(bigEndian: false, orientation: 6, withGps: true),
        Xmp = GpsXmp,
        Orientation = 6,
        DpiX = 300,
        DpiY = 300,
    };

    [Fact]
    public void Metadata_StripAll_RemovesEverything()
    {
        var m = MetadataProcessor.Apply(Full(), MetadataPolicy.StripAll);

        Assert.Null(m.Icc);
        Assert.Null(m.Exif);
        Assert.Null(m.Xmp);
        Assert.Equal((96.0, 96.0), (m.DpiX, m.DpiY));
        Assert.Equal(1, m.Orientation);
    }

    [Fact]
    public void Metadata_KeepColorProfile_KeepsOnlyTheIcc()
    {
        var full = Full();

        var m = MetadataProcessor.Apply(full, MetadataPolicy.KeepColorProfile);

        Assert.Equal(full.Icc, m.Icc);
        Assert.Null(m.Exif);
        Assert.Null(m.Xmp);
        Assert.Equal(96, m.DpiX);
    }

    [Fact]
    public void Metadata_KeepAllButGps_RemovesLocationFromExifAndXmp()
    {
        var full = Full();

        var m = MetadataProcessor.Apply(full, MetadataPolicy.KeepAllButGps);

        Assert.Equal(full.Icc, m.Icc);
        Assert.NotNull(m.Exif);
        Assert.True(ExifReader.HasGps(full.Exif!));
        Assert.False(ExifReader.HasGps(m.Exif));
        Assert.True(m.Exif.AsSpan().IndexOf(ExifTestData.Latitude) < 0);
        Assert.Equal(300, m.DpiX);

        string xmp = Encoding.UTF8.GetString(m.Xmp!);
        Assert.DoesNotContain("GPS", xmp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<dc:format>image/jpeg</dc:format>", xmp);
        Assert.Contains("exif:ExposureTime=\"1/250\"", xmp);
        Assert.StartsWith("<?xpacket begin=\"﻿\"", xmp);
        Assert.EndsWith("<?xpacket end=\"w\"?>", xmp);
    }

    [Fact]
    public void Metadata_KeepAll_KeepsEverythingButResetsOrientation()
    {
        var full = Full();

        var m = MetadataProcessor.Apply(full, MetadataPolicy.KeepAll);

        Assert.Equal(full.Icc, m.Icc);
        Assert.Equal(full.Xmp, m.Xmp);
        Assert.True(ExifReader.HasGps(m.Exif!));
        Assert.Equal(1, ExifReader.ReadOrientation(m.Exif!));
        Assert.Equal(1, m.Orientation);
        Assert.Equal(full.Exif!.Length, m.Exif!.Length);
    }

    [Fact]
    public void Metadata_NeverModifiesTheInput()
    {
        var full = Full();
        byte[] exif = full.Exif!.ToArray();

        _ = MetadataProcessor.Apply(full, MetadataPolicy.KeepAllButGps);
        _ = MetadataProcessor.Apply(full, MetadataPolicy.StripAll);

        Assert.Equal(exif, full.Exif);
        Assert.Equal(GpsXmp, full.Xmp);
        Assert.Equal(6, full.Orientation);
        Assert.Equal(300, full.DpiX);
    }

    [Fact]
    public void Metadata_XmpWithoutGps_IsKeptByteForByte()
    {
        var m = MetadataProcessor.Apply(new ImageMetadata { Xmp = ExifTestData.Xmp }, MetadataPolicy.KeepAllButGps);

        Assert.Equal(ExifTestData.Xmp, m.Xmp);
    }

    [Fact]
    public void Metadata_UnparseableBlocksAreDroppedWhenLocationMustGo()
    {
        var damaged = new ImageMetadata { Exif = [0x49, 0x49, 0x2A, 0x00, 0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3], Xmp = Encoding.UTF8.GetBytes("<x:xmpmeta><unclosed") };

        var m = MetadataProcessor.Apply(damaged, MetadataPolicy.KeepAllButGps);

        Assert.Null(m.Exif);
        Assert.Null(m.Xmp);
    }

    [Fact]
    public void Metadata_XmpWithADtdIsRejectedNotExpanded()
    {
        byte[] hostile = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;\">]><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">&b;</x:xmpmeta>");

        var m = MetadataProcessor.Apply(new ImageMetadata { Xmp = hostile }, MetadataPolicy.KeepAllButGps);

        Assert.Null(m.Xmp);
    }

    [Fact]
    public void Metadata_EmptyBlocksBecomeNull()
    {
        var m = MetadataProcessor.Apply(new ImageMetadata { Icc = [], Exif = [], Xmp = [] }, MetadataPolicy.KeepAll);

        Assert.Null(m.Icc);
        Assert.Null(m.Exif);
        Assert.Null(m.Xmp);
    }
}
