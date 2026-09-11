using System.Text.Json;

namespace StrangeSharpTerm.Model.Tests;

public class IdentityTests
{
    private static readonly Guid Sample = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public void AnIdIsWrittenAsSwiftWritesItAnUppercaseRawValueObject()
    {
        JsonSerializer.Serialize(new NodeId(Sample))
            .ShouldBe("""{"rawValue":"0F8FAD5B-D9CB-469F-A165-70867728950E"}""");
    }

    [Fact]
    public void ALowercaseIdIsAcceptedAsSwiftAcceptsIt()
    {
        JsonSerializer.Deserialize<NodeId>("""{"rawValue":"0f8fad5b-d9cb-469f-a165-70867728950e"}""")
            .ShouldBe(new NodeId(Sample));
    }

    [Fact]
    public void AMalformedIdIsRefusedRatherThanReplacedWithAFreshOne()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<NodeId>("""{"rawValue":"not-a-uuid"}"""));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<NodeId>("\"0f8fad5b-d9cb-469f-a165-70867728950e\""));
    }

    [Fact]
    public void TagsKeepTheOrderTheyWereGivenAndDropDuplicates()
    {
        new Folder { Name = "x", Tags = ["prod", "eu", "prod"] }.Tags.ShouldBe(new[] { "prod", "eu" });
    }

    [Fact]
    public void ATimestampSurvivesConversionToAndFromDateTimeOffset()
    {
        var moment = new DateTimeOffset(2026, 9, 11, 13, 28, 0, TimeSpan.Zero);
        Timestamp.FromDateTimeOffset(moment).ToDateTimeOffset().ShouldBe(moment);
        Timestamp.FromDateTimeOffset(Timestamp.ReferenceDate).SecondsSinceReferenceDate.ShouldBe(0);
    }
}
