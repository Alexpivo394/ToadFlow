using Shouldly;
using ToadFlow.Server.Schedules;

namespace ToadFlow.Server.Tests;

public class OutputFolderTemplateTests
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    [Test]
    public void Template_DateAndScheduleName() =>
        OutputFolderTemplate.Render(@"\\nas\exports\{расписание}\{дата}", DateTimeOffset.Parse("2026-10-23T23:30:00Z"), Moscow, "ЖК Север")
            .ShouldBe(@"\\nas\exports\ЖК Север\2026-10-24");

    [Test]
    public void Template_Time() =>
        OutputFolderTemplate.Render(@"D:\out\{дата}_{время}", DateTimeOffset.Parse("2026-10-23T17:05:00Z"), Moscow, "x")
            .ShouldBe(@"D:\out\2026-10-23_20-05");

    [Test]
    public void Template_ScheduleNameWithInvalidChars_Sanitized() =>
        OutputFolderTemplate.Render(@"D:\out\{расписание}", DateTimeOffset.Parse("2026-10-23T17:05:00Z"), Moscow, "ЖК «Юг»: RVT/NWC")
            .ShouldBe(@"D:\out\ЖК «Юг»_ RVT_NWC");

    [Test]
    public void Template_UnknownToken_ValidationError() =>
        OutputFolderTemplate.Validate(@"D:\out\{проект}").ShouldNotBeNull().ShouldContain("{проект}");

    [Test]
    public void Template_Empty_ValidationError() => OutputFolderTemplate.Validate("  ").ShouldNotBeNull();

    [Test]
    public void Template_Valid_NoError() => OutputFolderTemplate.Validate(@"\\nas\exports\{расписание}\{дата}").ShouldBeNull();
}
