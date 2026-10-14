using FlightLog.Application.Common;

namespace FlightLog.UnitTests.Application;

public sealed class PagingTests
{
    [Theory]
    [InlineData(1, 20, 1, 20, 0)]
    [InlineData(3, 10, 3, 10, 20)]
    [InlineData(0, 10, 1, 10, 0)]
    [InlineData(-5, 10, 1, 10, 0)]
    [InlineData(2, 0, 2, 1, 1)]
    [InlineData(2, 1000, 2, PageRequest.MaxPageSize, PageRequest.MaxPageSize)]
    public void PageRequest_clamps_to_safe_values(int page, int size, int expectedPage, int expectedSize, int expectedSkip)
    {
        var request = new PageRequest(page, size);

        request.SafePage.Should().Be(expectedPage);
        request.SafePageSize.Should().Be(expectedSize);
        request.Skip.Should().Be(expectedSkip);
    }

    [Theory]
    [InlineData(0, 20, 0)]
    [InlineData(20, 20, 1)]
    [InlineData(21, 20, 2)]
    public void PagedResult_computes_total_pages(int total, int size, int expected) =>
        new PagedResult<int>([], 1, size, total).TotalPages.Should().Be(expected);
}
