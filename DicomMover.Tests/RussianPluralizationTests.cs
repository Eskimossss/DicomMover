using DicomMover.Services;
using Xunit;

namespace DicomMover.Tests;

public sealed class RussianPluralizationTests
{
    [Theory]
    [InlineData(0, "0 исследований", "В очередь отправки на PACS добавлено 0 исследований.")]
    [InlineData(1, "1 исследование", "В очередь отправки на PACS добавлено 1 исследование.")]
    [InlineData(2, "2 исследования", "В очередь отправки на PACS добавлено 2 исследования.")]
    [InlineData(4, "4 исследования", "В очередь отправки на PACS добавлено 4 исследования.")]
    [InlineData(5, "5 исследований", "В очередь отправки на PACS добавлено 5 исследований.")]
    [InlineData(11, "11 исследований", "В очередь отправки на PACS добавлено 11 исследований.")]
    [InlineData(14, "14 исследований", "В очередь отправки на PACS добавлено 14 исследований.")]
    [InlineData(20, "20 исследований", "В очередь отправки на PACS добавлено 20 исследований.")]
    [InlineData(21, "21 исследование", "В очередь отправки на PACS добавлено 21 исследование.")]
    [InlineData(22, "22 исследования", "В очередь отправки на PACS добавлено 22 исследования.")]
    [InlineData(25, "25 исследований", "В очередь отправки на PACS добавлено 25 исследований.")]
    [InlineData(101, "101 исследование", "В очередь отправки на PACS добавлено 101 исследование.")]
    public void PluralizesStudiesCorrectly(int count, string expectedPlural, string expectedSentence)
    {
        Assert.Equal(expectedPlural, RussianPluralization.PluralizeStudies(count));
        Assert.Equal(expectedSentence, RussianPluralization.FormatStudiesEnqueued(count));
    }
}
