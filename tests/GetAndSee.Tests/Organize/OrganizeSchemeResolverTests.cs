using GetAndSee.Core.Organize;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Organize;

public sealed class OrganizeSchemeResolverTests
{
    [Fact]
    public void Brand_new_archive_uses_the_request_and_records_it()
    {
        SchemeResolution resolution = OrganizeSchemeResolver.Resolve(
            recorded: null, requested: OrganizeScheme.Month, requestedIsExplicit: false);

        resolution.Effective.ShouldBe(OrganizeScheme.Month);
        resolution.Warning.ShouldBeNull();
        resolution.ShouldRecord.ShouldBeTrue();
    }

    [Fact]
    public void Brand_new_archive_honours_an_explicit_request()
    {
        SchemeResolution resolution = OrganizeSchemeResolver.Resolve(
            recorded: null, requested: OrganizeScheme.Year, requestedIsExplicit: true);

        resolution.Effective.ShouldBe(OrganizeScheme.Year);
        resolution.Warning.ShouldBeNull();
        resolution.ShouldRecord.ShouldBeTrue();
    }

    [Fact]
    public void Defaulted_flag_silently_yields_to_the_recorded_scheme()
    {
        // A re-run that did not pass --organize-by must follow the archive's recorded layout without noise.
        SchemeResolution resolution = OrganizeSchemeResolver.Resolve(
            recorded: OrganizeScheme.YearMonth, requested: OrganizeScheme.Month, requestedIsExplicit: false);

        resolution.Effective.ShouldBe(OrganizeScheme.YearMonth);
        resolution.Warning.ShouldBeNull();
        resolution.ShouldRecord.ShouldBeFalse();
    }

    [Fact]
    public void Explicit_conflicting_flag_warns_and_keeps_the_recorded_scheme()
    {
        SchemeResolution resolution = OrganizeSchemeResolver.Resolve(
            recorded: OrganizeScheme.YearMonth, requested: OrganizeScheme.Flat, requestedIsExplicit: true);

        resolution.Effective.ShouldBe(OrganizeScheme.YearMonth);
        resolution.ShouldRecord.ShouldBeFalse();
        resolution.Warning.ShouldNotBeNull();
        resolution.Warning.ShouldContain("year-month");
        resolution.Warning.ShouldContain("flat");
        resolution.Warning.ShouldContain("reorganize");
    }

    [Fact]
    public void Explicit_flag_matching_the_recorded_scheme_is_silent()
    {
        SchemeResolution resolution = OrganizeSchemeResolver.Resolve(
            recorded: OrganizeScheme.Year, requested: OrganizeScheme.Year, requestedIsExplicit: true);

        resolution.Effective.ShouldBe(OrganizeScheme.Year);
        resolution.Warning.ShouldBeNull();
        resolution.ShouldRecord.ShouldBeFalse();
    }
}
