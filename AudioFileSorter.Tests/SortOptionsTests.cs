using AudioFileSorter.Model;

namespace AudioFileSorter.Tests;

public class SortOptionsTests
{
    [Theory]
    [InlineData(null, CopySpeed.Normal)]
    [InlineData("", CopySpeed.Normal)]
    [InlineData("normal", CopySpeed.Normal)]
    [InlineData(" Gentle ", CopySpeed.Gentle)]
    public void Copy_speed_parses_its_wire_values(string? value, CopySpeed expected)
    {
        Assert.True(SortOptions.TryParseCopySpeed(value, out var speed));
        Assert.Equal(expected, speed);
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("1")]
    public void An_unrecognised_copy_speed_is_rejected(string value)
    {
        Assert.False(SortOptions.TryParseCopySpeed(value, out _));
    }

    [Theory]
    [InlineData(CopySpeed.Normal, "normal")]
    [InlineData(CopySpeed.Gentle, "gentle")]
    public void Copy_speed_round_trips_through_its_wire_value(CopySpeed speed, string expected)
    {
        Assert.Equal(expected, SortOptions.ToWireValue(speed));
        Assert.True(SortOptions.TryParseCopySpeed(SortOptions.ToWireValue(speed), out var parsed));
        Assert.Equal(speed, parsed);
    }

    [Fact]
    public void Gentle_copies_one_book_at_a_time_whatever_normal_is()
    {
        Assert.Equal(1, SortOptions.ParallelismFor(CopySpeed.Gentle, 6));
        Assert.Equal(6, SortOptions.ParallelismFor(CopySpeed.Normal, 6));
    }

    [Fact]
    public void The_default_parallelism_stays_modest()
    {
        Assert.InRange(SortOptions.DefaultParallelism, 1, 8);
        Assert.Equal(SortOptions.DefaultParallelism, SortOptions.Default.MaxParallelism);
    }

    [Fact]
    public void Parallelism_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SortOptions { MaxParallelism = 0 });
    }

    [Fact]
    public void Destinations_are_not_created_unless_asked()
    {
        Assert.False(SortOptions.Default.CreateDestination);
    }
}
