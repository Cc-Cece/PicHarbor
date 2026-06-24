using System.CommandLine;
using GetAndSee.Cli.Commands;
using GetAndSee.Core.Organize;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Cli;

public sealed class CopyCommandTests
{
    [Fact]
    public void Organize_by_defaults_to_month_when_not_passed()
    {
        Command copy = CopyCommand.Build();

        ParseResult result = copy.Parse(["--dest", "X"]);

        result.GetValue(CopyCommand.OrganizeByOption).ShouldBe(OrganizeSchemes.MonthToken);
        // A defaulted flag is reported as implicit — this is how copy tells a defaulted flag from an
        // explicit one so it can silently yield to an archive's recorded scheme.
        (result.GetResult(CopyCommand.OrganizeByOption) is { Implicit: true }).ShouldBeTrue();
    }

    [Theory]
    [InlineData("month")]
    [InlineData("year-month")]
    [InlineData("year")]
    [InlineData("flat")]
    public void Organize_by_is_explicit_when_passed(string token)
    {
        Command copy = CopyCommand.Build();

        ParseResult result = copy.Parse(["--dest", "X", "--organize-by", token]);

        result.Errors.ShouldBeEmpty();
        result.GetValue(CopyCommand.OrganizeByOption).ShouldBe(token);
        (result.GetResult(CopyCommand.OrganizeByOption) is { Implicit: false }).ShouldBeTrue();
    }

    [Fact]
    public void Organize_by_rejects_an_unknown_value()
    {
        Command copy = CopyCommand.Build();

        ParseResult result = copy.Parse(["--dest", "X", "--organize-by", "weekly"]);

        result.Errors.ShouldNotBeEmpty();
    }
}
