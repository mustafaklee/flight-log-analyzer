using FlightLog.Application.Logs;
using FlightLog.Domain.Flights;

namespace FlightLog.UnitTests.Application;

public sealed class LogFormatTests
{
    [Fact]
    public void Ulog_magic_is_detected()
    {
        byte[] header = [0x55, 0x4C, 0x6F, 0x67, 0x01, 0x12, 0x35, 0x01, 0, 0, 0, 0, 0, 0, 0, 0];

        LogFormatDetector.Detect(header).Should().Be(LogFormat.Px4ULog);
    }

    [Fact]
    public void Dataflash_fmt_header_is_detected()
    {
        byte[] header = [0xA3, 0x95, 0x80, 0x80, 0x59, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        LogFormatDetector.Detect(header).Should().Be(LogFormat.ArduPilotDataFlash);
    }

    [Theory]
    [InlineData(new byte[] { 0x55, 0x4C, 0x6F, 0x67 })]
    [InlineData(new byte[] { 0xA3, 0x95, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[0])]
    public void Other_content_is_not_recognized(byte[] header) =>
        LogFormatDetector.Detect(header).Should().BeNull();

    [Theory]
    [InlineData("a.ulg", LogFormat.Px4ULog)]
    [InlineData("A.ULG", LogFormat.Px4ULog)]
    [InlineData("00000042.BIN", LogFormat.ArduPilotDataFlash)]
    [InlineData("flight.log", null)]
    [InlineData("flight", null)]
    public void Extensions_map_to_formats(string fileName, LogFormat? expected) =>
        LogFormatDetector.FromExtension(fileName).Should().Be(expected);

    [Theory]
    [InlineData("../../etc/passwd.ulg", "passwd.ulg")]
    [InlineData("C:\\logs\\run 1.ulg", "run 1.ulg")]
    [InlineData("a<b>c|d?.bin", "a_b_c_d_.bin")]
    [InlineData("...", "log")]
    [InlineData(".hidden.ulg", "hidden.ulg")]
    [InlineData(null, "log")]
    public void File_names_are_reduced_to_safe_display_names(string? input, string expected) =>
        FileNames.Sanitize(input).Should().Be(expected);

    [Fact]
    public void Long_file_names_keep_their_extension()
    {
        var name = FileNames.Sanitize(new string('x', 400) + ".ulg");

        name.Should().HaveLength(FlightLogFile.FileNameMaxLength).And.EndWith(".ulg");
    }
}
