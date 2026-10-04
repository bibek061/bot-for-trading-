using System.Globalization;
using AutopilotQuant.Core.Replay;
using Xunit;

namespace AutopilotQuant.Tests;

public class HistoricalBarCsvReaderTests
{
    [Fact]
    public async Task Reader_Parses_Required_Columns_Regardless_Of_Header_Order()
    {
        const string csv = """
            Volume,Close,Timestamp,Symbol,Low,Open,High
            120,5000.25,2025-01-02T09:35:00-05:00,MES,4999.75,5000,5000.50
            """;

        var bars = await HistoricalBarCsvReader.ReadAsync(new StringReader(csv));

        var bar = Assert.Single(bars);
        Assert.Equal("MES", bar.Symbol);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 9, 35, 0, TimeSpan.FromHours(-5)), bar.Timestamp);
        Assert.Equal(5000.25m, bar.Close);
        Assert.Equal(120, bar.Volume);
    }

    [Fact]
    public async Task Reader_Handles_Quoted_Symbols_And_Rejects_Missing_Columns()
    {
        const string csv = """
            Symbol,Timestamp,Open,High,Low,Close,Volume
            "MES",2025-01-02T09:35:00Z,5000,5000.5,4999.5,5000.25,1
            """;
        var bars = await HistoricalBarCsvReader.ReadAsync(new StringReader(csv));
        Assert.Equal("MES", Assert.Single(bars).Symbol);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            HistoricalBarCsvReader.ReadAsync(new StringReader("Symbol,Timestamp\nMES,2025-01-02T00:00:00Z")));
    }

    [Fact]
    public async Task Reader_Reports_Invalid_Row_Values()
    {
        const string csv = """
            Symbol,Timestamp,Open,High,Low,Close,Volume
            MES,invalid,1,1,1,1,1
            """;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            HistoricalBarCsvReader.ReadAsync(new StringReader(csv)));

        Assert.Contains("line 2", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Timestamp", error.Message);
    }

    [Fact]
    public async Task Reader_Rejects_Timestamp_Without_Explicit_Offset()
    {
        const string csv = """
            Symbol,Timestamp,Open,High,Low,Close,Volume
            MES,2025-01-02T09:35:00,5000,5000.5,4999.5,5000.25,1
            """;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            HistoricalBarCsvReader.ReadAsync(new StringReader(csv)));

        Assert.Contains("explicit timezone offset", error.Message);
    }
}
